// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Models\Configuration\ModelConfigurationReadback.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 配置保存回读的完整定义安全指纹, 不将分区或连接正文放入网页 DTO.

using PbiUtils.Infrastructure.Storage;
using PbiUtils.Infrastructure.Models.Pbip;
using PbiUtils.Application.Models;
using System.Text;
using PbiUtils.Application;
using PbiUtils.Core;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiUtils.Infrastructure.Models.Configuration;

/// <summary>ModelInfo 只包含展示字段, 分区、命名表达式和锁注解必须另行精确回读证明.</summary>
public static class ModelConfigurationReadback
{
    /// <summary>完整模型序列化只在内存中规范化键序与换行, 对外仅保留 SHA256.</summary>
    public static string Fingerprint(TOM.Database database)
    {
        var model = ModelConfigurationOwnership.Definition(database.Model);
        return ModelConfigurationOwnership.Hash(Encoding.UTF8.GetBytes(database.CompatibilityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + model!.ToJsonString()));
    }

    /// <summary>从已核实 PBIP 绑定加载实际落盘 TOM, 前后复核同一完整定义基线.</summary>
    public static Task<string> FingerprintFileAsync(ProjectSnapshot snapshot, CancellationToken token) => Task.Run(async () =>
    {
        var reader = new ProjectReader();
        var before = await reader.ReadAsync(snapshot.ProjectPath, token).ConfigureAwait(false);
        if (before.BaselineId != snapshot.BaselineId) throw new AppException("STALE_BASELINE", "配置回读期间定义已变化", 3);
        var root = Path.GetDirectoryName(snapshot.ProjectPath)!;
        var descriptor = snapshot.Files.Single(file => Path.GetFileName(file.Path) == "definition.pbism");
        var modelRoot = Path.GetDirectoryName(Path.Combine(root, descriptor.Path))!;
        UserDataStore.Check(modelRoot);
        using var database = snapshot.ModelFormat == "TMDL" ? TOM.TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(modelRoot, "definition"))
            : TOM.JsonSerializer.DeserializeDatabase(await File.ReadAllTextAsync(Path.Combine(modelRoot, "model.bim"), token).ConfigureAwait(false));
        var fingerprint = Fingerprint(database);
        if ((await reader.ReadAsync(snapshot.ProjectPath, token).ConfigureAwait(false)).BaselineId != snapshot.BaselineId)
            throw new AppException("STALE_BASELINE", "配置回读期间定义已变化", 3);
        return fingerprint;
    }, token);
}
