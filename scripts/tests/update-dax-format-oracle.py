# FilePath    : pbi-utils\scripts\tests\update-dax-format-oracle.py
# Author      : jiaopengzi
# Blog        : https://jiaopengzi.com
# Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
# Description : 显式联网采集 SQLBI 双样式 DAX 基准, 常规产品和测试不调用此脚本.

"""从已授权的任务文本提取度量或重用固定输入, 保存完整 API 请求和响应.

此脚本只连接固定 SQLBI 端点, 不扫描用户模型. 默认离线校验已有快照;
只有显式 --refresh 才发送 Fixtures/DaxFormatting/inputs.json 中的 DAX.
失败退出非零并保留原 oracle.json, 不以空输出或部分响应更新基准.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import urllib.request


ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests/PbiUtils.UnitTests/Fixtures/DaxFormatting"
ENDPOINT = "https://api.daxformatter.com/api/daxtextformatmulti"
OPTIONS = {
    "skipSpaceAfterFunctionName": False,
    "listSeparator": ",",
    "decimalSeparator": ".",
    "callerApp": "pbi-utils-formatting-regression",
    "callerVersion": "1",
}
HEADER = "OracleMeasure =\n"
CLIENT_REVISION = "2979aeb6abaaf799f69fb403fda67dee6bcf4428"
TOKEN_PATTERN = re.compile(r'''//[^\r\n]*|--[^\r\n]*|/\*[\s\S]*?\*/|(?:[dD][tT])?"(?:[^"]|"")*"|'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|(?P<word>[^\W\d]\w*)|&&|\|\||<=|>=|<>|==|:=|=>|\S''')


def read_json(path: Path):
    """读取指定 UTF-8 JSON; 文件损坏或缺失时直接失败, 不产生默认空数据."""
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value):
    """将完整数据写到同目录临时文件后替换, 写入错误直接传播."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    temporary.replace(path)


def extract_measures(text: str):
    """严格提取任务 TMDL 的全部度量正文, 剥离四个结构 tab 而不改内部空白.

    支持任务中的普通表达式和三反引号表达式; 元数据不进入 DAX.
    空表达式、重复名称、不闭合 fence 或未知缩进都会失败.
    """
    tmdl_match = re.search(r"^```TMDL\n(.*?)^```\s*$", text, re.MULTILINE | re.DOTALL)
    if tmdl_match is None:
        raise ValueError("任务文件缺少唯一 TMDL 块")
    tmdl = tmdl_match.group(1)
    lines = tmdl.splitlines(keepends=True)
    measure_headers = [i for i, line in enumerate(lines) if line.startswith("\t\tmeasure ")]
    cases = []
    names = set()
    for position, start in enumerate(measure_headers):
        match = re.fullmatch(r"\t\tmeasure (.+?) =\s*(```)?\n?", lines[start])
        if match is None:
            raise ValueError(f"未识别的度量定义: {lines[start]!r}")
        name, fence = match.groups()
        if name in names:
            raise ValueError(f"重复度量: {name}")
        names.add(name)
        end = measure_headers[position + 1] if position + 1 < len(measure_headers) else len(lines)
        expression_lines = []
        closed = not fence
        for line in lines[start + 1:end]:
            if line.startswith("\t\t\t\t"):
                content = line[4:]
                if fence and content.strip() == "```":
                    closed = True
                    break
                expression_lines.append(content)
            elif not line.strip():
                expression_lines.append(line)
            else:
                if fence:
                    raise ValueError(f"三反引号表达式未闭合: {name}")
                break
        expression = "".join(expression_lines).strip("\r\n")
        if not expression.strip() or not closed:
            raise ValueError(f"空或不完整表达式: {name}")
        cases.append({"id": f"production-{position + 1:03}", "sourceKind": "production", "name": name, "expression": expression})
    if not cases:
        raise ValueError("未提取任何度量")
    logo_match = re.search(r"^```dax\nlogo_image2 =[^\n]*\n(.*?)^```", text, re.MULTILINE | re.DOTALL)
    if logo_match is None:
        raise ValueError("任务文件缺少 logo_image2 原始示例")
    logo = "\n".join(line[4:] if line.startswith("    ") else line for line in logo_match.group(1).splitlines()).strip("\n")
    cases.append({"id": "task-logo", "sourceKind": "task-example", "name": "logo_image2", "expression": logo})
    return tmdl, cases


def additional_cases():
    """提供与任务无关业务数据隔离的边界输入, 仅生成无网络副作用的测试数据."""
    expressions = {
        "simple-multiple-arguments": "DIVIDE(1,2,BLANK())",
        "nested-single-argument": "ABS(SUM(Sales[Amount]))",
        "switch-pairs": 'SWITCH(TRUE(),[Amount]>100,"Large",[Amount]>0,"Small","None")',
        "row-name-expression-pairs": 'ROW("First",1,"Second",DIVIDE(4,2))',
        "addcolumns-name-expression-pairs": 'ADDCOLUMNS(Sales,"Net",Sales[Amount]-Sales[Cost],"Rate",DIVIDE(Sales[Cost],Sales[Amount]))',
        "summarize-group-and-pair": 'SUMMARIZE(Sales,Sales[Category],"Amount",SUM(Sales[Amount]),"Quantity",SUM(Sales[Quantity]))',
        "omitted-rank-argument": "RANKX(ALL(Sales[Category]),[Amount],,DESC,DENSE)",
        "unary-and-scientific": "-1 + 1e-3 - -2",
        "escaped-table-and-column": "SUM('O''Brien'[A]]B])",
        "escaped-string": 'IF([Amount]>0,"a  b","x""y")',
        "unicode-references": "SUM('中文 表'[金额])+[利润]",
        "datetime-literal": 'dt"2026-10-30T13:45:00"',
        "line-comment": "VAR Total=1 // keep  two spaces  \nRETURN Total+2",
        "block-comment": "/* config  value */\nVAR Total=1\n/* final  value */\nRETURN Total",
        "multiline-comment": "/* first\n  second  line\n*/\nVAR Total=1 RETURN Total",
        "multiline-string": 'VAR Message="first\n second" RETURN Message',
        "nested-var": "VAR OuterTotal=VAR InnerTotal=1 RETURN InnerTotal+2 VAR OtherTotal=3 RETURN OuterTotal+OtherTotal",
        "iterator-var": "SUMX(Sales,VAR Net=Sales[Amount]-Sales[Cost] RETURN Net*2)",
        "table-constructor": '{(1,"One"),(2,"Two"),(3,"Three")}',
        "boolean-condition": 'IF(ISBLANK([Amount])||LEFT([Label],5)="data:"||LEFT([Label],7)="http://"||LEFT([Label],8)="https://",[Amount],0)',
        "binary-call-continuations": "DIVIDE([Amount],[Quantity])+SUM(Sales[Tax])-1",
        "membership": 'IF([Category] IN {"A","B","C"},1,0)',
        "preserved-lowercase": "var lower_case=sum(Sales[Amount]) return divide(lower_case,2)",
        "redundant-table-quotes": "SUM('Sales'[Amount])",
    }
    return [{"id": f"boundary-{name}", "sourceKind": "boundary", "name": name, "expression": expression} for name, expression in expressions.items()]


def request_batch(cases, style):
    """发送完整已授权输入到固定端点并返回原始 JSON; 超时/HTTP/格式错误不重试伪装成功."""
    request = {"dax": [HEADER + case["expression"] for case in cases], "maxLineLength": style, **OPTIONS}
    data = json.dumps(request, ensure_ascii=False).encode("utf-8")
    http_request = urllib.request.Request(ENDPOINT, data, {"content-type": "application/json; charset=utf-8", "accept": "application/json"}, method="POST")
    with urllib.request.urlopen(http_request, timeout=60) as response:
        raw = response.read().decode("utf-8-sig")
    results = json.loads(raw)
    if not isinstance(results, list) or len(results) != len(cases):
        raise ValueError(f"API 返回数量异常: 预期 {len(cases)}")
    for case, result in zip(cases, results, strict=True):
        if not isinstance(result, dict) or result.get("errors") or not result.get("formatted"):
            raise ValueError(f"API 拒绝 {case['id']}: {json.dumps(result, ensure_ascii=False)}")
    return request, results


def normalize_response(result):
    """仅规范 CRLF、移除定义头和末尾服务换行, 不修改任何内部布局.

    任务的三个库存度量含首部大段注释, 服务把定义名丢为 ` =`.
    完整响应原样保留, 此已观察到的异常只影响剥离头, 不修订正文.
    """
    formatted = result["formatted"].replace("\r\n", "\n")
    if formatted.startswith(HEADER):
        prefix = HEADER
    elif formatted.startswith(" =\n"):
        prefix = " =\n"
    else:
        raise ValueError(f"API 返回非预期定义头: {formatted[:50]!r}")
    return formatted[len(prefix):].removesuffix("\n")


def validate_snapshot(inputs, snapshot):
    """离线确认所有输入、两种响应与标准输出完整匹配, 不调用被测格式化器."""
    if len(inputs["cases"]) != len(snapshot["cases"]):
        raise ValueError("输入与基准数量不一致")
    expected_dax = [HEADER + case["expression"] for case in inputs["cases"]]
    for style, value in (("Long", 0), ("Short", 1)):
        request = snapshot[f"request{style}"]
        if request != {"dax": expected_dax, "maxLineLength": value, **OPTIONS}:
            raise ValueError(f"{style} 请求与输入/固定样式参数不一致")
    for source, case in zip(inputs["cases"], snapshot["cases"], strict=True):
        if any(source[key] != case[key] for key in source):
            raise ValueError(f"输入与基准不一致: {source['id']}")
        for style in ("Long", "Short"):
            response = case[f"response{style}"]
            if response.get("errors") or not response.get("formatted"):
                raise ValueError(f"快照存在服务失败: {source['id']} {style}")
            if normalize_response(response) != case[f"expected{style}"]:
                raise ValueError(f"响应与标准输出不一致: {source['id']} {style}")
            before = list(TOKEN_PATTERN.finditer(source["expression"]))
            after = list(TOKEN_PATTERN.finditer(case[f"expected{style}"]))
            if len(before) != len(after):
                raise ValueError(f"服务改变了词元数量: {source['id']} {style}")
            for original, standard in zip(before, after, strict=True):
                if original[0] == standard[0]:
                    continue
                if original["word"] and standard["word"] and original[0].casefold() == standard[0].casefold():
                    continue
                raise ValueError(f"服务改变了非大小写词元: {source['id']} {style}")


def main():
    """解析显式导入/刷新选项, 完整采集成功后写快照, 默认只做离线完整性检查."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, help="从已授权任务 Markdown 重新提取输入")
    parser.add_argument("--refresh", action="store_true", help="显式将固定测试 DAX 发送到 SQLBI API")
    parser.add_argument("--rebuild", action="store_true", help="从已经保存的完整响应离线重建基准")
    args = parser.parse_args()
    inputs_path = FIXTURES / "inputs.json"
    if args.source:
        source = args.source.read_bytes()
        tmdl, cases = extract_measures(source.decode("utf-8-sig").replace("\r\n", "\n"))
        inputs = {"sourceFile": args.source.name, "sourceSha256": hashlib.sha256(source).hexdigest(), "productionMeasureCount": len(cases) - 1, "cases": cases + additional_cases()}
        write_json(inputs_path, inputs)
        (FIXTURES / "production.tmdl").write_text(tmdl, encoding="utf-8", newline="\n")
    inputs = read_json(inputs_path)
    tmdl_names = re.findall(r"^\t\tmeasure ([^\r\n]+?) =", (FIXTURES / "production.tmdl").read_text(encoding="utf-8"), re.MULTILINE)
    recorded_names = [case["name"] for case in inputs["cases"] if case["sourceKind"] == "production"]
    if tmdl_names != recorded_names or len(tmdl_names) != inputs["productionMeasureCount"]:
        raise ValueError("原始 TMDL 的完整有序度量名称与输入不一致")
    output_path = FIXTURES / "oracle.json"
    if args.refresh and args.rebuild:
        parser.error("--refresh 与 --rebuild 不能同时使用")
    if args.refresh:
        long_request, long_results = request_batch(inputs["cases"], 0)
        write_json(FIXTURES / "response-long.json", {"collectedAtUtc": datetime.now(timezone.utc).isoformat(), "request": long_request, "responses": long_results})
        short_request, short_results = request_batch(inputs["cases"], 1)
        write_json(FIXTURES / "response-short.json", {"collectedAtUtc": datetime.now(timezone.utc).isoformat(), "request": short_request, "responses": short_results})
    if args.rebuild:
        long_snapshot = read_json(FIXTURES / "response-long.json")
        short_snapshot = read_json(FIXTURES / "response-short.json")
        long_request, long_results = long_snapshot["request"], long_snapshot["responses"]
        short_request, short_results = short_snapshot["request"], short_snapshot["responses"]
    if args.refresh or args.rebuild:
        snapshot = {
            "schemaVersion": 1,
            "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
            "endpoint": ENDPOINT,
            "sourceFile": inputs["sourceFile"],
            "sourceSha256": inputs["sourceSha256"],
            "serviceVersion": "Not returned by service",
            "clientSource": f"https://github.com/sql-bi/DaxFormatter/tree/{CLIENT_REVISION}/mcp/src/daxformatter",
            "requestLong": long_request,
            "requestShort": short_request,
            "productionMeasureCount": inputs["productionMeasureCount"],
            "cases": [{**case, "responseLong": long_result, "responseShort": short_result, "expectedLong": normalize_response(long_result), "expectedShort": normalize_response(short_result)} for case, long_result, short_result in zip(inputs["cases"], long_results, short_results, strict=True)],
        }
        validate_snapshot(inputs, snapshot)
        write_json(output_path, snapshot)
    validate_snapshot(inputs, read_json(output_path))
    print(f"PASS: {inputs['productionMeasureCount']} production measures, {len(inputs['cases'])} total cases, 2 styles; snapshot complete.")


if __name__ == "__main__":
    main()
