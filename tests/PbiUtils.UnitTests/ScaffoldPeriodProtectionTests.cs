// FilePath    : pbi-utils\tests\PbiUtils.UnitTests\ScaffoldPeriodProtectionTests.cs
// Author      : jiaopengzi
// Blog        : https://jiaopengzi.com
// Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
// Description : 验证期间缩减不留下存活 DAX 和模型元数据中的悬空度量值引用.

using PbiUtils.Application.Code.Dax;
using PbiUtils.Infrastructure.Calendar.TimeIntelligence;
using PbiUtils.Application;
using PbiUtils.Core;
using PbiUtils.Infrastructure.Scaffolding;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace PbiUtils.UnitTests;

/// <summary>仅验证内存 TOM 依赖保护, 不代表真实引擎删除或文件保存验收.</summary>
public sealed class ScaffoldPeriodProtectionTests
{
    /// <summary>所有存活 DAX 根保护待删除期间, 包含不在普通度量值列表中的表达式.</summary>
    [Theory]
    [InlineData("measure")]
    [InlineData("column")]
    [InlineData("partition")]
    [InlineData("role")]
    [InlineData("calculation")]
    [InlineData("format")]
    [InlineData("detail")]
    [InlineData("kpi")]
    [InlineData("function")]
    [InlineData("selection-none")]
    [InlineData("selection-multiple-format")]
    public void SurvivingDaxRootsPreventDeletion(string kind)
    {
        using var database = Database();
        var table = database.Model.Tables[0];
        const string expression = "[00_AC_销售]";
        switch (kind)
        {
            case "measure": table.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = expression }); break;
            case "column": table.Columns.Add(new TOM.CalculatedColumn { Name = "C", DataType = TOM.DataType.Int64, Expression = expression }); break;
            case "partition": table.Partitions.Add(new TOM.Partition { Name = "Calculated", Source = new TOM.CalculatedPartitionSource { Expression = "{" + expression + "}" } }); break;
            case "role":
                var role = new TOM.ModelRole { Name = "Reader" }; database.Model.Roles.Add(role);
                role.TablePermissions.Add(new TOM.TablePermission { Name = table.Name, Table = table, FilterExpression = expression + " > 0" }); break;
            case "calculation": table.CalculationGroup = new TOM.CalculationGroup(); table.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem { Name = "Item", Expression = expression }); break;
            case "format": table.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "1", FormatStringDefinition = new TOM.FormatStringDefinition { Expression = expression } }); break;
            case "detail": table.DefaultDetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "{" + expression + "}" }; break;
            case "kpi": table.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "1", KPI = new TOM.KPI { TargetExpression = expression } }); break;
            case "function": database.Model.Functions.Add(new TOM.Function { Name = "Lib.Consumer", Expression = "() => " + expression }); break;
            default:
                table.CalculationGroup = new TOM.CalculationGroup();
                var selection = new TOM.CalculationGroupExpression
                {
                    Expression = kind == "selection-none" ? expression : "SELECTEDMEASURE()",
                    FormatStringDefinition = kind == "selection-none" ? null : new TOM.FormatStringDefinition { Expression = expression }
                };
                if (kind == "selection-none") table.CalculationGroup.NoSelectionExpression = selection;
                else table.CalculationGroup.MultipleOrEmptySelectionExpression = selection;
                break;
        }
        AssertBlocked(database);
    }

    /// <summary>待删除集合内部依赖可整体清理, 字符串、注释、M 和其他表列不构成期间引用.</summary>
    [Fact]
    public void RemovedClosureAndNonReferencesAllowDeletionWithoutMutation()
    {
        using var database = Database();
        var model = database.Model; var table = model.Tables[0];
        var dependent = new TOM.Measure { Name = "02_ΔPD_销售", Expression = "[00_AC_销售] - 1" }; table.Measures.Add(dependent);
        var other = new TOM.Table { Name = "Other" }; model.Tables.Add(other);
        other.Columns.Add(new TOM.DataColumn { Name = "00_AC_销售", SourceColumn = "00_AC_销售", DataType = TOM.DataType.Int64 });
        other.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "SUM('Other'[00_AC_销售]) + LEN(\"[00_AC_销售]\") /* [00_AC_销售] */ // [00_AC_销售]\n" });
        model.Expressions.Add(new TOM.NamedExpression { Name = "M", Kind = TOM.ExpressionKind.M, Expression = "let x = [00_AC_销售] in x" });
        table.Partitions.Add(new TOM.Partition { Name = "Native", Source = new TOM.QueryPartitionSource { Query = "SELECT [00_AC_销售] FROM T" } });
        table.Annotations.Add(new TOM.Annotation { Name = "Business", Value = "[00_AC_销售]" });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        ScaffoldPeriodProtection.RequireUnreferenced(model, [table.Measures[0], dependent]);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>转义表名、度量值名及 RETURN 前缀按实际词元识别.</summary>
    [Theory]
    [InlineData("'Measures'' data'[00_AC_销售]")]
    [InlineData("VAR x = 1 RETURN [00_AC_销售]")]
    [InlineData("[00_AC_销售] /* arbitrary */ + 0")]
    public void ActualReferencesAreNotHiddenByQuotingOrKeywords(string expression)
    {
        using var database = Database();
        database.Model.Tables[0].Measures.Add(new TOM.Measure { Name = "Consumer", Expression = expression });
        AssertBlocked(database);
        Assert.True(DaxFormatter.TryReadMeasureReferences("'A''B'[销售]]额]", out var references));
        var (table, name) = Assert.Single(references);
        Assert.Equal("A'B", table); Assert.Equal("销售]额", name);
    }

    /// <summary>无法证明完整的表达式不允许清理, 即使未读到目标名称.</summary>
    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("SUM(")]
    [InlineData("1 + ?")]
    public void UnknownDaxBlocksDeletion(string expression)
    {
        using var database = Database();
        database.Model.Tables[0].Measures.Add(new TOM.Measure { Name = "Consumer", Expression = expression });
        AssertBlocked(database);
    }

    /// <summary>真实增量刷新源和轮询均是 M, 只豁免策略自己的已知字段, 不跳过同表存活 DAX.</summary>
    [Theory]
    [InlineData("source")]
    [InlineData("polling")]
    [InlineData("source-with-consumer")]
    public void BasicRefreshPolicyMExpressionsDoNotBlockUnreferencedRemoval(string field)
    {
        using var database = Database();
        var table = database.Model.Tables[0];
        table.RefreshPolicy = new TOM.BasicRefreshPolicy
        {
            SourceExpression = field.StartsWith("source", StringComparison.Ordinal) ? "let Source = #table({\"00_AC_销售\"}, {{1}}) in Source" : null,
            PollingExpression = field == "polling" ? "#datetime(2024, 1, 1, 0, 0, 0)" : null
        };
        if (field == "source-with-consumer")
        {
            table.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "[00_AC_销售]" });
            AssertBlocked(database);
            return;
        }
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        ScaffoldPeriodProtection.RequireUnreferenced(database.Model, [table.Measures[0]]);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>仅真实表策略可豁免 M 字段, 其他元数据的同名容器及未知策略类型不获豁免.</summary>
    [Theory]
    [InlineData("basic")]
    [InlineData("unknown")]
    public void MetadataNamedRefreshPolicyDoesNotBypassReferenceProtection(string policyType)
    {
        using var database = Database();
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"refreshPolicy\":{\"policyType\":\"" + policyType + "\",\"sourceExpression\":\"[00_AC_销售]\"}}"
            }
        });
        AssertBlocked(database);
    }

    /// <summary>透视、翻译和 Q&A 绑定均保护实际度量值; 未知 Q&A 绑定不作为无人使用证明.</summary>
    [Theory]
    [InlineData("perspective")]
    [InlineData("perspective-all")]
    [InlineData("translation")]
    [InlineData("linguistic")]
    [InlineData("linguistic-unknown")]
    public void MetadataReferencesPreventDeletion(string kind)
    {
        using var database = Database();
        var model = database.Model; var table = model.Tables[0];
        if (kind.StartsWith("perspective", StringComparison.Ordinal))
        {
            var perspective = new TOM.Perspective { Name = "Report" }; model.Perspectives.Add(perspective);
            var visible = new TOM.PerspectiveTable { Name = table.Name, Table = table, IncludeAll = kind == "perspective-all" }; perspective.PerspectiveTables.Add(visible);
            if (kind == "perspective") visible.PerspectiveMeasures.Add(new TOM.PerspectiveMeasure { Name = table.Measures[0].Name, Measure = table.Measures[0] });
        }
        else
        {
            var culture = new TOM.Culture { Name = "en-US" }; model.Cultures.Add(culture);
            if (kind == "translation") culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = table.Measures[0], Property = TOM.TranslatedProperty.Caption, Value = "Current" });
            else culture.LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = kind == "linguistic-unknown" ? "{\"Binding\":{\"ConceptualEntity\":\"Measures' data\",\"Unknown\":true}}"
                    : "{\"Entities\":{\"current\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Measures' data\",\"ConceptualProperty\":\"00_AC_销售\"}}}}}"
            };
        }
        AssertBlocked(database);
    }

    /// <summary>空删除集合无需分析无关未知表达式, 保持可选段禁用的无副作用语义.</summary>
    [Fact]
    public void EmptyRemovalSkipsUnrelatedInvalidExpressions()
    {
        using var database = Database();
        database.Model.Tables[0].Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "?" });
        ScaffoldPeriodProtection.RequireUnreferenced(database.Model, []);
    }

    /// <summary>移动预检只豁免明确重写的旧期间 DAX 根, 存活外部引用和元数据绑定继续保护.</summary>
    [Theory]
    [InlineData("internal")]
    [InlineData("external")]
    [InlineData("metadata")]
    public void ReplacementRootsAreExcludedWithoutExemptingExternalBindings(string kind)
    {
        using var database = Database();
        var model = database.Model; var table = model.Tables[0];
        var moving = table.Measures[0];
        var rewritten = new TOM.Measure { Name = "02_ΔPD_销售", Expression = "[00_AC_销售] - 1" }; table.Measures.Add(rewritten);
        if (kind == "external") table.Measures.Add(new TOM.Measure { Name = "Consumer", Expression = "[00_AC_销售]" });
        if (kind == "metadata")
        {
            var culture = new TOM.Culture { Name = "en-US" }; model.Cultures.Add(culture);
            culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = moving, Property = TOM.TranslatedProperty.Caption, Value = "Current" });
        }
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        if (kind == "internal") ScaffoldPeriodProtection.RequireUnreferenced(model, [moving], [moving, rewritten]);
        else Assert.Equal("CONFIGURATION_PERIOD_REFERENCED", Assert.Throws<AppException>(() => ScaffoldPeriodProtection.RequireUnreferenced(model, [moving], [moving, rewritten])).Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>标准 Q&A 表实体声明不会把该表所有期间永久锁定, 未知绑定形状仍需拒绝.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandardLinguisticTableDeclarationAllowsRemovalButExtensionsBlock(bool extended)
    {
        using var database = Database();
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"table\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Measures' data\""
                    + (extended ? ",\"Unknown\":true" : "") + "}}}}}"
            }
        });
        if (extended) AssertBlocked(database);
        else ScaffoldPeriodProtection.RequireUnreferenced(database.Model, [database.Model.Tables[0].Measures[0]]);
    }

    /// <summary>真实模板 Q&A 的关系表绑定声明不引用所有度量; 属性绑定、未知扩展与未知位置继续阻止删除.</summary>
    [Theory]
    [InlineData("table")]
    [InlineData("property")]
    [InlineData("extended")]
    [InlineData("unknown-position")]
    public void LinguisticRelationshipTableBindingDoesNotLockEveryMeasure(string shape)
    {
        using var database = Database();
        var binding = "{\"ConceptualEntity\":\"Measures' data\"" + (shape switch
        {
            "property" => ",\"ConceptualProperty\":\"00_AC_销售\"",
            "extended" => ",\"Unknown\":true",
            _ => ""
        }) + "}";
        var relationships = "{\"has_attribute\":{\"Binding\":" + binding
            + ",\"State\":\"Generated\",\"Roles\":{\"subject\":{\"Target\":{\"Entity\":\"table\"}},\"object\":{\"Target\":{\"Entity\":\"another_property\"}}}}}";
        var content = shape == "unknown-position" ? "{\"Unknown\":{\"Relationships\":" + relationships + "}}"
            : "{\"Version\":\"1.0.0\",\"Entities\":{\"table\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Measures' data\"}}}},\"Relationships\":" + relationships + "}";
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json, Content = content }
        });
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        if (shape == "table") ScaffoldPeriodProtection.RequireUnreferenced(database.Model, [database.Model.Tables[0].Measures[0]]);
        else AssertBlocked(database);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }

    /// <summary>Q&A 官方常用 Table/Measure 表示法与规范字段同样保护实际度量引用.</summary>
    [Theory]
    [InlineData("Measure")]
    [InlineData("Column")]
    public void CommonLinguisticPropertyBindingProtectsRemovedMeasure(string property)
    {
        using var database = Database();
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"value\":{\"Definition\":{\"Binding\":{\"Table\":\"Measures' data\",\"" + property + "\":\"00_AC_销售\"}}}}}"
            }
        });
        AssertBlocked(database);
    }

    /// <summary>官方互斥的两种绑定表示法不能混用优先级来隐藏待删除对象引用.</summary>
    [Fact]
    public void MixedLinguisticBindingFormatsCannotHideReference()
    {
        using var database = Database();
        database.Model.Tables.Add(new TOM.Table { Name = "Other" });
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"value\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Other\",\"ConceptualProperty\":\"Other\",\"Table\":\"Measures' data\",\"Measure\":\"00_AC_销售\"}}}}}"
            }
        });
        AssertBlocked(database);
    }

    /// <summary>即使没有同时出现两种表字段, 混合属性字段也不能用规范属性名遮住真实常用度量引用.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedLinguisticPropertyFormatsCannotHideReference(bool commonTable)
    {
        using var database = Database();
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"value\":{\"Definition\":{\"Binding\":{\"" + (commonTable ? "Table" : "ConceptualEntity")
                    + "\":\"Measures' data\",\"ConceptualProperty\":\"Other\",\"Measure\":\"00_AC_销售\"}}}}}"
            }
        });
        AssertBlocked(database);
    }

    /// <summary>通用小写表别名也不能覆盖同一对象中的 Q&A 正式目标身份.</summary>
    [Fact]
    public void LowercaseTableAliasCannotHideLinguisticReference()
    {
        using var database = Database();
        database.Model.Tables.Add(new TOM.Table { Name = "Other" });
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = "{\"Entities\":{\"value\":{\"Definition\":{\"Binding\":{\"table\":\"Other\",\"ConceptualEntity\":\"Measures' data\",\"ConceptualProperty\":\"00_AC_销售\"}}}}}"
            }
        });
        AssertBlocked(database);
    }

    /// <summary>未知数组或嵌套伪装路径不能借标准关系表绑定获得豁免.</summary>
    [Theory]
    [InlineData("array")]
    [InlineData("nested-metadata")]
    public void MalformedLinguisticRelationshipPathKeepsProtection(string shape)
    {
        using var database = Database();
        const string relationship = "{\"relation\":{\"Binding\":{\"ConceptualEntity\":\"Measures' data\"}}}";
        database.Model.Cultures.Add(new TOM.Culture
        {
            Name = "en-US",
            LinguisticMetadata = new TOM.LinguisticMetadata
            {
                ContentType = TOM.ContentType.Json,
                Content = shape == "array" ? "{\"Relationships\":[" + relationship + "]}"
                    : "{\"Unknown\":{\"linguisticMetadata\":{\"content\":{\"Relationships\":" + relationship + "}}}}"
            }
        });
        AssertBlocked(database);
    }

    /// <summary>建立单期间对象的完整内存模型, 不创建文件或调用外部引擎.</summary>
    private static TOM.Database Database()
    {
        var database = new TOM.Database { Name = "period-protection", CompatibilityLevel = 1702, Model = new TOM.Model() };
        var table = new TOM.Table { Name = "Measures' data" }; database.Model.Tables.Add(table);
        table.Measures.Add(new TOM.Measure { Name = "00_AC_销售", Expression = "1" });
        return database;
    }

    /// <summary>调用正式保护入口并验证稳定错误码及原始对象保留.</summary>
    private static void AssertBlocked(TOM.Database database)
    {
        var before = TOM.JsonSerializer.SerializeDatabase(database);
        Assert.Equal("CONFIGURATION_PERIOD_REFERENCED", Assert.Throws<AppException>(() => ScaffoldPeriodProtection.RequireUnreferenced(database.Model, [database.Model.Tables[0].Measures[0]])).Code);
        Assert.Equal(before, TOM.JsonSerializer.SerializeDatabase(database));
    }
}
