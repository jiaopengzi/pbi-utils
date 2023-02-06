# -*- encoding: utf-8 -*-
"""
@File           :   dax_en.py
@Time           :   2022-11-10, 周四, 21:41
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   英文 dax 模板
"""

TEMPLATE_DAX_LIST = {
        "navigationButtonBackgroundColor": """
VAR pageOrdinal = 0
VAR permissionName = "reportPage"
/* dax template */
VAR PowerBIUsers = 
    FILTER ( ALL( 'C03_PowerBIUsers' ), [userPrincipalName] = USERPRINCIPALNAME () )
VAR pageList = 
    FILTER( PowerBIUsers, [permissionName] = permissionName )
VAR pageOrdinalList = 
    SELECTCOLUMNS ( pageList, "@VALUE", INT( [value] ) )
VAR TrueFalse = 
    pageOrdinal IN pageOrdinalList
VAR page = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinal )
VAR ColorYes = 
    CONCATENATEX( page, [navigationButtonBackgroundColorYes] )
VAR ColorNo = 
    CONCATENATEX( page, [navigationButtonBackgroundColorNo] )
VAR Result = 
    IF ( TrueFalse, ColorYes, ColorNo )
RETURN
    Result""",
        "navigationButtonDisplayName"    : """
VAR pageOrdinal = 0
/*dax template*/
VAR page = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinal )
VAR navigationButtonDisplayName = 
    CONCATENATEX( page, [navigationButtonDisplayName] )
RETURN
    navigationButtonDisplayName""",
        "navigationButtonPageDisplayName": """
VAR pageOrdinalYes = 0
VAR pageOrdinalNo  = 2
VAR permissionName = "reportPage"
/*dax template*/
VAR PowerBIUsers = 
    FILTER ( ALL( 'C03_PowerBIUsers' ), [userPrincipalName] = USERPRINCIPALNAME () )
VAR pageList = 
    FILTER( PowerBIUsers, [permissionName] = permissionName )
VAR pageOrdinalList = 
    SELECTCOLUMNS ( pageList, "@VALUE", INT( [value] ) )
VAR TrueFalse = 
    pageOrdinalYes IN pageOrdinalList
VAR pageYes = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinalYes )
VAR pageNo = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinalNo )
VAR pageDisplayNameYes = 
    CONCATENATEX( pageYes, [DisplayName] )
VAR pageDisplayNameNo = 
    CONCATENATEX( pageNo, [DisplayName] )
VAR Result = 
    IF ( TrueFalse, pageDisplayNameYes, pageDisplayNameNo )
RETURN
    Result""",
        "navigationButtonTextColor"      : """
VAR pageOrdinalYes = 0
VAR permissionName = "reportPage"
/*dax template*/
VAR PowerBIUsers = 
    FILTER ( ALL( 'C03_PowerBIUsers' ), [userPrincipalName] = USERPRINCIPALNAME () )
VAR pageList = 
    FILTER( PowerBIUsers, [permissionName] = permissionName )
VAR pageOrdinalList = 
    SELECTCOLUMNS ( pageList, "@VALUE", INT( [value] ) )
VAR TrueFalse = 
    pageOrdinalYes IN pageOrdinalList
VAR pageYes = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinalYes )
VAR ColorYes = 
    CONCATENATEX( pageYes, [navigationButtonTextColorYes] )
VAR ColorNo = 
    CONCATENATEX( pageYes, [navigationButtonTextColorNo] )
VAR Result = 
    IF ( TrueFalse, ColorYes, ColorNo )
RETURN
    Result""",
        "navigationButtonTooltip"        : """
VAR pageOrdinalYes = 0
VAR permissionName = "reportPage"
/*dax template*/
VAR PowerBIUsers = 
    FILTER ( ALL( 'C03_PowerBIUsers' ), [userPrincipalName] = USERPRINCIPALNAME () )
VAR pageList = 
    FILTER( PowerBIUsers, [permissionName] = permissionName )
VAR pageOrdinalList = 
    SELECTCOLUMNS ( pageList, "@VALUE", INT( [value] ) )
VAR TrueFalse = 
    pageOrdinalYes IN pageOrdinalList
VAR pageYes = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinalYes )
VAR ColorYes = 
    CONCATENATEX( pageYes, [navigationButtonTooltipYes] )
VAR ColorNo = 
    CONCATENATEX( pageYes, [navigationButtonTooltipNo] )
VAR Result = 
    IF ( TrueFalse, ColorYes, ColorNo )
RETURN
    Result""",
        "pageTitleBackgroundColor"       : """
/* dax template*/
VAR pageOrdinal = 0
VAR page = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinal )
VAR pageTitleBackgroundColor = 
    CONCATENATEX( page, [pageTitleBackgroundColor] )
RETURN
    pageTitleBackgroundColor""",
        "pageTitleText"                  : """
/* dax template*/
VAR pageOrdinal = 0
VAR page = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinal )
VAR pageTitleText = 
    CONCATENATEX( page, [pageTitleText] )
RETURN
    pageTitleText""",
        "pageTitleTextColor"             : """
/* dax template*/
VAR pageOrdinal = 0
VAR page = 
    FILTER( ALL( C02_ReportPages ), C02_ReportPages[Ordinal] = pageOrdinal )
VAR pageTitleTextColor = 
    CONCATENATEX( page, [pageTitleTextColor] )
RETURN
    pageTitleTextColor"""
}

RLS_DAX_TEMPLATE = """
VAR T0 =
    FILTER ( 'C03_PowerBIUsers', [userPrincipalName] = USERPRINCIPALNAME () )
VAR rlsT0 =
    FILTER ( T0, [permissionName] = "${name}" )
VAR rlsDimensionValueList =
    SELECTCOLUMNS ( rlsT0, "@VALUE", [value] )
VAR dimensionValueAll = 
    SELECTCOLUMNS ( ${tableName}, "@VALUE", CONVERT( ${tableColumn}, STRING ) )
VAR dimensionTable =
    INTERSECT ( dimensionValueAll, rlsDimensionValueList )
VAR rlsResult =
    IF ( ISEMPTY ( dimensionTable ), FALSE (), TRUE () )
RETURN
    rlsResult"""

DAX_REPORT_VISUAL_TEMPLATE = {
        "ImageUrl"   : """
VAR SVG_start_image = 
    "data:image/svg+xml;utf8,"
VAR config_table = 
    FILTER ( 'C01_ReportVisualTemplates', 'C01_ReportVisualTemplates'[name] = "${name}" )
VAR config_value = 
    CONCATENATEX ( config_table, [value] )
VAR _status = 
    SEARCH ( "<svg*</svg>", config_value, 1, BLANK() )
VAR result =  IF( ISBLANK ( _status ), config_value, SVG_start_image & config_value )
RETURN
    result""",
        "NotImageUrl": """
VAR config_table = 
    FILTER ( 'C01_ReportVisualTemplates', 'C01_ReportVisualTemplates'[name] = "${name}" )
VAR config_value = 
    CONCATENATEX ( config_table, [value] )
RETURN
    config_value"""
}