// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\PbipFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 构造独立最小 PBIP 文件夹具, 不依赖已撤下的初始化业务.

namespace PbiUtils.UnitTests;

/// <summary>只向调用方拥有的临时目录写入契约夹具, 不模拟 Desktop 导出验收.</summary>
internal static class PbipFixture
{
    /// <summary>创建含单表单度量值的 PBIP, 返回工程入口供真实读写服务测试.</summary>
    internal static string Create(string root)
    {
        Write("测试.pbip", """{"version":"1.0","artifacts":[{"report":{"path":"report"}}]}""");
        Write("report/definition.pbir", """{"version":"4.0","datasetReference":{"byPath":{"path":"../model"}}}""");
        Write("report/definition/report.json", "{}");
        Write("report/definition/pages/pages.json", """{"pageOrder":[]}""");
        Write("model/definition.pbism", """{"version":"1.0"}""");
        Write("model/model.bim", """{"name":"test","compatibilityLevel":1606,"model":{"culture":"en-US","tables":[{"name":"销售","description":"原说明","measures":[{"name":"销售额","expression":"1"}]}]}}""");
        return Path.Combine(root, "测试.pbip");

        /// <summary>创建夹具父目录并写入固定测试正文.</summary>
        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }
}
