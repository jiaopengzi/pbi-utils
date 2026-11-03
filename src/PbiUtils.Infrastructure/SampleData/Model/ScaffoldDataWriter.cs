// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\SampleData\Model\ScaffoldDataWriter.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 流式输出预算内 CSV, 通过显式映射组合业务表并保留未知关联为空.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using PbiUtils.Application.SampleData;
using PbiUtils.Application.Scaffolding;
using PbiUtils.Core;
using PbiUtils.Infrastructure.SampleData;
using TOM = Microsoft.AnalysisServices.Tabular;

using PbiUtils.Infrastructure.Code.PowerQuery;
using PbiUtils.Infrastructure.Scaffolding;

namespace PbiUtils.Infrastructure.SampleData.Model;

/// <summary>模板列保持原名与类型, 不通过近似名称猜测业务含义.</summary>
internal static class ScaffoldDataWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    internal static readonly IReadOnlyDictionary<string, string> RoleFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["regions"] = SampleTables.FileRegion,
        ["provinces"] = SampleTables.FileProvince,
        ["cities"] = SampleTables.FileCity,
        ["districts"] = SampleTables.FileDistrict,
        ["products"] = SampleTables.FileProduct,
        ["stores"] = SampleTables.FileStore,
        ["customers"] = SampleTables.FileCustomer,
        ["stock"] = SampleTables.FileInventory,
        ["orders"] = SampleTables.FileOrder,
        ["order-details"] = SampleTables.FileOrderItem,
        ["targets-month-to-day"] = SampleTables.FileSaleTarget,
    };
    private static readonly string[][] SampleTypes = [ ["I", "S", "S", "S", "N", "N"], ["I", "S", "S", "S", "D", "I", "S", "N", "N", "D"],
        ["I", "S", "S", "D", "S", "D", "S", "S"], ["I", "S", "I", "S", "D"], ["I", "S", "S", "D", "D", "S", "S"],
        ["I", "S", "S", "N", "N", "I", "N"], ["I", "I", "S", "D", "N"], ["I", "I", "S", "S", "I", "S", "N", "N"],
        ["I", "I", "I", "S", "S", "S", "N", "N"], ["I", "I", "I", "S", "N", "N"], ["I", "I", "I", "S", "N", "N"] ];
    private static readonly Dictionary<string, string?[]> RetailColumns = new(StringComparer.Ordinal)
    {
        ["regions"] = [null, null, "简称", null, "办公地城市ID", "办公地城市", "纬度", "经度"],
        ["provinces"] = [null, null, "省ID", "省全称", "省简称1", "省简称2", "纬度", "经度"],
        ["cities"] = [null, "省ID", "城市ID", "城市", "纬度", "经度"],
        ["districts"] = [null, "城市ID", "区县ID", "区县", "纬度", "经度"],
        ["products"] = [null, "产品ID", "产品分类", "产品名称", "产品销售价格", "产品成本价格"],
        ["stores"] = [null, null, "门店名称", null, "开业日期", null, null, "纬度", "经度", "闭店日期"],
        ["customers"] = [null, "客户ID", "客户名称", "客户生日", "客户性别", "注册日期", "客户行业", "客户职业"],
        ["stock"] = [null, "入库产品ID", "入库产品数量", null, "入库日期"],
        ["orders"] = [null, "订单ID", null, "下单日期", "送货日期", "客户ID", "销售渠道"],
        ["order-details"] = [null, "订单ID", "产品ID", "产品销售价格", "折扣比例", "产品销售数量", "产品销售金额"],
    };

    /// <summary>复用示例纯生成器输出原始十一表; 新建可明确替换 staging 同名 CSV, 已有模型仍默认拒绝.</summary>
    /// <param name="replaceExisting">仅新建私有 staging 放行固定十一文件替换, 不允许覆盖目录或任意文件.</param>
    internal static SampleCsvStorage Generate(string stage, SampleDataOptions options, ScaffoldWriteBudget budget, CancellationToken token, IProgress<ScaffoldProgress>? progress = null, bool replaceExisting = false)
    {
        var directory = Path.Combine(stage, "data");
        token.ThrowIfCancellationRequested();
        if (File.Exists(directory) || SampleTables.AllFiles.Any(file => Directory.Exists(Path.Combine(directory, file))
            || !replaceExisting && Path.Exists(Path.Combine(directory, file))))
            throw new AppException("SCAFFOLD_OBJECT_CONFLICT", "模板已有与模拟数据同名的文件, 请调整模板后重试", 2);
        if (replaceExisting)
            foreach (var file in SampleTables.AllFiles.Select(file => Path.Combine(directory, file)).Where(File.Exists))
                budget.ReleaseReplaced(new FileInfo(file).Length);
        var storage = new SampleCsvStorage(directory);
        _ = new SampleDataGenerator(options, SampleResources.Load(), new SampleProgress(progress)).GenerateAll(new BudgetStorage(storage, budget), token);
        return storage;
    }

    /// <summary>将固定业务源逐行适配现有模型结构, 区县只使用真实地理字典.</summary>
    internal static IEnumerable<string[]> MapRows(TOM.Table table, string role, SampleCsvStorage? storage, bool retail, List<string> warnings)
    {
        if (storage is null || !RoleFiles.TryGetValue(role, out var file)) return [];
        if (role == "targets-month-to-day") return TargetRows(table, storage.Read(file));
        var columns = table.Columns.OfType<TOM.DataColumn>().ToArray();
        var header = SampleTables.Header(file);
        var indexes = columns.Select(column => retail ? Array.IndexOf(RetailColumns[role], column.SourceColumn) : Index(header, column.SourceColumn)).ToArray();
        for (var i = 0; i < columns.Length; i++)
        {
            if (indexes[i] >= 0 && columns[i].DataType != SampleType(file, indexes[i]))
                throw new AppException("SCAFFOLD_COLUMN_TYPE_MISMATCH", "映射列类型与样例源声明不一致", 2);
            if (!retail && indexes[i] < 0 && !(role == "stores" && columns[i].SourceColumn is "F_05_区县ID" or "F_06_区县"))
                throw new AppException("SCAFFOLD_COLUMN_MAPPING", "模板包含没有明确样例映射的字段", 2);
        }
        if (retail && columns.Any(column => column.Name.Contains("组织ID", StringComparison.Ordinal) || column.Name == "销售员工ID"))
            warnings.Add($"{table.Name}: 示例没有组织或员工身份, 对应关联字段保持空值");
        if (role == "stores") warnings.Add($"{table.Name}: 门店源仅有城市信息, 区县无法唯一确定时保持空值");
        return Convert();

        // 真实城市对应多个区县时不能任意指定其中一个; 仅唯一映射可填充.
        IEnumerable<string[]> Convert()
        {
            var districts = SampleResources.Load().Districts.GroupBy(d => d.CityId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
            var stores = retail && role is "stock" or "orders" ? storage.Read(SampleTables.FileStore).ToDictionary(row => row[1], row => row[0], StringComparer.Ordinal) : null;
            foreach (var source in storage.Read(file))
            {
                var row = indexes.Select(index => index < 0 ? "" : source[index]).ToArray();
                if (retail)
                {
                    if (role is "regions" or "provinces") Set(columns, row, "PbiUtilsRegionId", source[1]);
                    if (role == "stores") Set(columns, row, "PbiUtilsStoreId", source[0]);
                    if (role is "stock" or "orders")
                    {
                        var code = source[role == "stock" ? 3 : 2];
                        if (stores is null || !stores.TryGetValue(code, out var id)) throw new AppException("SCAFFOLD_FOREIGN_KEY", "样例门店外键没有对应身份", 4);
                        Set(columns, row, "PbiUtilsStoreId", id);
                    }
                }
                if (role == "stores" && int.TryParse(source[5], Invariant, out var city) && districts.TryGetValue(city, out var district))
                {
                    Set(columns, row, retail ? "区县ID" : "F_05_区县ID", district.DistrictId.ToString(Invariant));
                    Set(columns, row, retail ? "区县" : "F_06_区县", district.Name);
                }
                // 零售模板价格声明为整数, 不静默截断小数; 非整数数据要求用户明确修改模板类型.
                for (var i = 0; i < columns.Length; i++)
                    if (columns[i].DataType == TOM.DataType.Int64 && row[i].Length > 0 && decimal.TryParse(row[i], NumberStyles.Number, Invariant, out var number) && number != decimal.Truncate(number))
                        throw new AppException("SCAFFOLD_COLUMN_TYPE_MISMATCH", "示例数值与模板整数列不兼容, 请调整映射类型", 2);
                yield return row;
            }
        }
    }

    /// <summary>沿用月初至月末均摊并保留两位小数的模板规则.</summary>
    private static IEnumerable<string[]> TargetRows(TOM.Table table, IEnumerable<string[]> rows)
    {
        var columns = table.Columns.OfType<TOM.DataColumn>().ToArray();
        foreach (var source in rows)
        {
            var month = DateOnly.Parse(source[3], Invariant);
            var days = DateTime.DaysInMonth(month.Year, month.Month);
            var amount = Math.Round(decimal.Parse(source[4], Invariant) / days, 2, MidpointRounding.ToEven).ToString(Invariant);
            for (var day = 1; day <= days; day++)
            {
                var date = new DateOnly(month.Year, month.Month, day).ToString("yyyy-MM-dd", Invariant);
                yield return columns.Select(column => column.SourceColumn switch
                {
                    "F_00_自动编号" => source[0],
                    "F_01_省ID" or "省ID" => source[1],
                    "F_02_省简称" or "省简称" => source[2],
                    "F_03_目标日期" or "目标日期" => date,
                    "F_04_每日目标" or "每日目标" => amount,
                    _ => throw new AppException("SCAFFOLD_COLUMN_MAPPING", "销售目标表存在未知字段", 2),
                }).ToArray();
            }
        }
    }

    /// <summary>实际逐行写盘并回读行数, 字段数与类型转换由固定模型声明约束.</summary>
    /// <param name="replaceExisting">仅新建样例组合允许替换已计量的 staging 适配 CSV, 其他入口保持默认禁止.</param>
    internal static ScaffoldTableResult Write(string stage, string relative, string tableName, IReadOnlyList<string> header,
        IEnumerable<string[]> rows, ScaffoldWriteBudget budget, CancellationToken token, bool replaceExisting = false)
    {
        var path = Path.Combine(stage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (replaceExisting && File.Exists(path)) budget.ReleaseReplaced(new FileInfo(path).Length);
        budget.Reserve(3);
        using (var writer = new StreamWriter(new FileStream(path, replaceExisting ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(true)))
        {
            writer.NewLine = "\n";
            WriteLine(writer, header, budget);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.Length != header.Count) throw new AppException("SCAFFOLD_COLUMN_MAPPING", "数据行与模型字段数不同", 2);
                WriteLine(writer, row, budget);
            }
        }
        return Inspect(stage, relative, tableName, header, token);
    }

    /// <summary>回读既有 CSV 的表头、列宽、真实行数和字节数, 复用原始模拟表时不重写或重复计量.</summary>
    internal static ScaffoldTableResult Inspect(string stage, string relative, string tableName, IReadOnlyList<string> header, CancellationToken token)
    {
        var path = Path.Combine(stage, relative);
        long count = 0;
        using (var parser = new TextFieldParser(path, Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false })
        {
            parser.SetDelimiters(",");
            var actualHeader = parser.ReadFields();
            if (actualHeader is null || !actualHeader.SequenceEqual(header)) throw new AppException("SCAFFOLD_CSV_VERIFY", "生成数据表头回读不一致", 4);
            while (!parser.EndOfData)
            {
                token.ThrowIfCancellationRequested();
                if (parser.ReadFields()?.Length != header.Count) throw new AppException("SCAFFOLD_CSV_VERIFY", "生成数据列数回读不一致", 4);
                count++;
            }
        }
        return new(tableName, count, new FileInfo(path).Length, count == 0 ? "empty" : "populated");
    }

    /// <summary>固定 CSV 与模型类型构造 M, 空值保持 null, 路径只来自内部文件映射.</summary>
    /// <param name="relative">内部映射的相对 CSV 路径, 统一分隔符并按 M 文本规则转义.</param>
    /// <param name="columns">实际数据列和类型, 以 SourceColumn 生成转换列表.</param>
    /// <returns>与源路径和全部列绑定的固定 M 查询, 使用 LF 且不附加尾换行.</returns>
    /// <exception cref="AppException">列类型没有受支持的 M 转换规则时拒绝生成.</exception>
    internal static string CsvExpression(string relative, IEnumerable<TOM.DataColumn> columns)
    {
        var definitions = columns.ToArray();
        var types = string.Join(", ", definitions.Select(c => "{" + PowerQueryLiterals.Text(c.SourceColumn) + ", " + MType(c.DataType) + "}"));
        var names = string.Join(", ", definitions.Select(c => PowerQueryLiterals.Text(c.SourceColumn)));
        var path = PowerQueryLiterals.Text("/" + relative.Replace('\\', '/'));
        return $$"""
            let
                Source = Csv.Document(File.Contents(PbiUtilsProjectRoot & {{path}}), [Delimiter=",", Encoding=65001, QuoteStyle=QuoteStyle.Csv]),
                Headers = Table.PromoteHeaders(Source, [PromoteAllScalars=true]),
                Nulls = Table.ReplaceValue(Headers, "", null, Replacer.ReplaceValue, {{{names}}}),
                Typed = Table.TransformColumnTypes(Nulls, {{{types}}}, "en-US")
            in
                Typed
            """;
    }
    /// <summary>用户模型名称可能包含文件系统分隔符, 非普通名称使用稳定摘要文件名.</summary>
    internal static string FileName(string tableName)
    {
        try { return ScaffoldValidation.ProjectName(tableName) + ".csv"; }
        catch (AppException) { return "table-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tableName))) + ".csv"; }
    }
    /// <summary>十一表固定源类型, 用户映射必须完整一致, 不依赖内容猜测类型.</summary>
    internal static TOM.DataType SampleType(string file, int column) => SampleTypes[Index(SampleTables.AllFiles, file)][column] switch
    { "I" => TOM.DataType.Int64, "N" => TOM.DataType.Double, "D" => TOM.DataType.DateTime, _ => TOM.DataType.String };
    /// <summary>只支持有明确 M 转换定义的真实数据列类型.</summary>
    private static string MType(TOM.DataType type) => type switch { TOM.DataType.String => "type text", TOM.DataType.Int64 => "Int64.Type", TOM.DataType.Double or TOM.DataType.Decimal => "type number", TOM.DataType.DateTime => "type datetime", TOM.DataType.Boolean => "type logical", _ => throw new AppException("SCAFFOLD_COLUMN_TYPE", "模板列类型不支持本地 CSV", 2) };
    /// <summary>逐字段标准 CSV 转义, 序列化字节先计入预算.</summary>
    private static void WriteLine(StreamWriter writer, IReadOnlyList<string> values, ScaffoldWriteBudget budget)
    {
        var line = string.Join(',', values.Select(Csv)) + "\n";
        budget.Reserve(Encoding.UTF8.GetByteCount(line));
        writer.Write(line);
    }
    /// <summary>固定引用所有字段, 防止空行或首尾空白被解析器忽略.</summary>
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    /// <summary>显式查找完整字段名.</summary>
    private static int Index(IReadOnlyList<string> values, string value) { for (var i = 0; i < values.Count; i++) if (values[i] == value) return i; return -1; }
    /// <summary>仅更新明确命名的区县字段.</summary>
    private static void Set(TOM.DataColumn[] columns, string[] row, string name, string value) { var index = Array.FindIndex(columns, c => c.SourceColumn == name); if (index >= 0) row[index] = value; }

    /// <summary>给已有流式样例存储添加逐行预算, 不缓存整表.</summary>
    private sealed class BudgetStorage(SampleCsvStorage inner, ScaffoldWriteBudget budget) : ISampleTableStorage
    {
        /// <summary>表头与 BOM 在创建前计量.</summary>
        public ISampleRowWriter Create(string fileName, IReadOnlyList<string> header) { Reserve(header, true); return new BudgetWriter(inner.Create(fileName, header), budget); }
        /// <summary>新建流程不得追加.</summary>
        public ISampleRowWriter Append(string fileName) => throw new NotSupportedException();
        /// <summary>沿用固定表名受限读取.</summary>
        public IEnumerable<string[]> Read(string fileName) => inner.Read(fileName);
        /// <summary>沿用固定表名存在判断.</summary>
        public bool Exists(string fileName) => inner.Exists(fileName);
        /// <summary>使用全引用上界计量, 保证实际 CSV 不超过预算.</summary>
        private void Reserve(IReadOnlyList<string> row, bool bom) => budget.Reserve(Encoding.UTF8.GetByteCount(string.Join(',', row.Select(Csv))) + 1 + (bom ? 3 : 0));
    }
    /// <summary>每行在真实写入前计量.</summary>
    private sealed class BudgetWriter(ISampleRowWriter inner, ScaffoldWriteBudget budget) : ISampleRowWriter
    {
        /// <summary>保守上界预留字节再写入.</summary>
        public void Write(IReadOnlyList<string> row) { budget.Reserve(Encoding.UTF8.GetByteCount(string.Join(',', row.Select(Csv))) + 1); inner.Write(row); }
        /// <summary>传播真实刷新错误.</summary>
        public void Dispose() => inner.Dispose();
    }
    /// <summary>样例生成完成只占准备阶段75%, 不提前宣称预览或发布完成.</summary>
    private sealed class SampleProgress(IProgress<ScaffoldProgress>? progress) : IProgress<SampleDataProgress>
    {
        /// <summary>同步转发固定阶段, 不创建迟到进度回调.</summary>
        public void Report(SampleDataProgress value) => progress?.Report(new(15 + Math.Clamp(value.Percent, 0, 100) * .6, "scaffold.preparing"));
    }
}
