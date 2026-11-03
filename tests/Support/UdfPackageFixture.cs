// FilePath    : pbi-utils\tests\Support\UdfPackageFixture.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 构造独立标准 UDF ZIP 包, 供共享服务和 MCP 的受控 Git 传输复用.

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PbiUtils.Tests.Support;

/// <summary>仅生成固定测试包字节, 不依赖产品解析器、网络或文件系统.</summary>
internal static class UdfPackageFixture
{
    /// <summary>构造多函数与依赖的标准包, 所有函数携带精确 manifest 身份注解.</summary>
    internal static byte[] Create(string id, string version, (string Name, string Expression, string Description)[] functions,
        string readme = "", object[]? dependencies = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var tmdl = string.Concat(functions.Select(function => $"/// {function.Description}\nfunction '{function.Name}' = {function.Expression}\n\tannotation DAXLIB_PackageId = {id}\n\tannotation DAXLIB_PackageVersion = {version}\n"));
            foreach (var (name, text) in new[] { ("manifest.daxlib", JsonSerializer.Serialize(new { id, version, dependencies = dependencies ?? [] })),
                ("lib/functions.tmdl", tmdl), ("README.md", readme) })
            {
                var entry = zip.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text);
            }
        }
        return output.ToArray();
    }

}
