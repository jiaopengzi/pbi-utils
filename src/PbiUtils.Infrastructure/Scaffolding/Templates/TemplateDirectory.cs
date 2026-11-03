// FilePath    : pbi-utils\src\PbiUtils.Infrastructure\Scaffolding\Templates\TemplateDirectory.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 仅在主动管理固定模板目录时无覆盖发布首次说明文件.

using PbiUtils.Infrastructure.Storage;
using System.Text;
using PbiUtils.Core;

namespace PbiUtils.Infrastructure.Scaffolding.Templates;

/// <summary>根说明不进入任何模板, 不参与模板基线或只读目录发现.</summary>
public sealed class TemplateDirectory(string userRoot)
{
    private const string Readme = """
        自定义 PBIP 模板 / Custom PBIP templates

        每个直接子目录是一个独立模板, 目录名就是界面显示名, 不翻译或替换.
        Each direct child directory is one template. Its directory name is its display name.

        结构示例 / Example:
          templates/pbip/
            readme.txt
            MyTemplate/
              MyTemplate.pbip
              MyTemplate.Report/definition.pbir
              MyTemplate.SemanticModel/definition.pbism
              MyTemplate.SemanticModel/definition/ (or model.bim)

        请完整复制一个 PBIP 工程到新的直接子目录, 不要只复制 .pbip 文件.
        模板必须有且只有一个 .pbip 入口, 可位于根目录或直接 pbip/ 子目录, 并通过相对路径绑定工程内唯一的本地报表和语义模型.
        报表和模型目录必须一并保留, 所有绑定均须位于该模板目录内部, 不得指向外部路径或在线模型.
        Copy the entire PBIP project into a new direct child directory, rather than only its .pbip file.
        The template must contain exactly one .pbip entry at its root or in its direct pbip/ subfolder. It must bind a local report and semantic model
        through relative paths within the same template. Preserve both directories and all required files.
        External paths and online model bindings are not supported.

        blank 为内置模板的保留目录名, 不区分大小写. 自定义模板请使用其他目录名.
        The name blank is reserved for the built-in template, ignoring case. Choose another name.

        复制或修改完成后, 返回应用的自定义模板管理并点击刷新. 无效项会显示原因, 不会自动修复或覆盖.
        After copying or editing, return to custom template management and refresh. Invalid items show a reason;
        the application does not automatically repair or overwrite them.

        不跟随符号链接, 目录联接或其他重解析点. 请复制真实文件, 不要链接到其他目录.
        Symbolic links, directory junctions and other reparse points are not followed. Copy actual files.

        目录发现和结构检查不会执行或验证外部数据源, Power Query M 或 DAX. 刷新及计算须另行核实.
        Template discovery and structural validation do not execute or validate external data sources,
        Power Query M or DAX. Verify data refresh and calculations separately.

        本文件仅首次创建, 已有文件及用户修改按原始字节保留. 它不属于任何模板, 不影响模板内容基线.
        This file is created once. Existing files and user edits are preserved byte for byte.
        It is outside individual templates and does not change their content baselines.
        """;

    /// <summary>宿主固定用户根下的模板目录, 不能由网页提交任意路径.</summary>
    public string RootPath { get; } = Path.GetFullPath(Path.Combine(userRoot, "templates", "pbip"));

    /// <summary>创建目录和首次说明后返回根路径, 失败时不伪报就绪.</summary>
    public Task<string> EnsureReadmeAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(RootPath);
            Directory.CreateDirectory(RootPath);
            Check(RootPath);
            var path = Path.Combine(RootPath, "readme.txt");
            Check(path);
            if (File.Exists(path)) return RootPath;
            if (Directory.Exists(path)) throw Error("TEMPLATE_DIRECTORY_FAILED", "模板说明位置已被目录占用, 请检查后重试");
            temporary = Path.Combine(RootPath, ".readme-" + Guid.NewGuid().ToString("N") + ".tmp");
            Check(temporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(Readme));
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Check(path);
            Check(temporary);
            // 完整写入后无覆盖移动; 并发实例或用户先发布的文件始终优先保留.
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { Check(path); }
            Check(path);
            if (!File.Exists(path)) throw Error("TEMPLATE_DIRECTORY_FAILED", "模板说明未能创建, 请检查后重试");
            return RootPath;
        }
        catch (UnauthorizedAccessException) { throw Error("TEMPLATE_ACCESS_DENIED", "无法写入模板说明, 请检查目录权限"); }
        catch (IOException) { throw Error("TEMPLATE_DIRECTORY_FAILED", "无法创建模板目录或说明, 请检查后重试"); }
        finally
        {
            if (temporary is not null)
            {
                Check(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }, cancellationToken);

    /// <summary>沿用所有祖先及节点的重解析点保护, 模板错误不包含真实路径.</summary>
    private static void Check(string path)
    {
        try { UserDataStore.Check(path); }
        catch (AppException error) when (error.Code == "UNSAFE_USER_DATA_PATH")
        { throw Error("TEMPLATE_PATH_UNSAFE", "模板路径包含链接或重解析点"); }
    }

    /// <summary>稳定错误代码和安全提示不回显本机路径或底层异常.</summary>
    private static AppException Error(string code, string message) => new(code, message, 3);
}
