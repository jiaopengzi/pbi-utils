# -*- encoding: utf-8 -*-
"""
@File           :   column_value_en.py
@Time           :   2022-11-10, 周四, 21:43
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   中文 column_value
"""

REPORT_VISUAL_COLUMN_NAME = {
        "name"         : "*度量值名称",
        "value"        : "*赋值",
        "dataCategory" : "*类别",
        # "measureTable": "*度量值主表",
        "status"       : "*是否启用",
        "description"  : "度量值描述",
        "displayFolder": "度量值文件夹"
}

DATA_CATEGORY = {
        "RefreshTime"    : "报表刷新时间",
        "Uncategorized"  : "未分类",
        "Address"        : "地址",
        "Place"          : "位置",
        "City"           : "城市",
        "County"         : "县",
        "StateOrProvince": "省/自治区/直辖市",
        "PostalCode"     : "邮政编码",
        "Country"        : "国家/地区",
        "Continent"      : "洲",
        "Latitude"       : "纬度",
        "Longitude"      : "经度",
        "WebUrl"         : "Web URL",
        "ImageUrl"       : "图片 URL",
        "Barcode"        : "条形码"
}

COLUMN_NAME = {
        "ordinal"                           : "ID",
        "name"                              : "url名称",
        "displayName"                       : "显示名称",
        "displayOption"                     : "页面视图",
        "height"                            : "高度",
        "width"                             : "宽度",
        "verticalAlignment"                 : "垂直对齐",
        "visibility"                        : "隐藏页",
        "pageTitleText"                     : "页标题",
        "pageTitleTextColor"                : "页标题颜色",
        "pageTitleBackgroundColor"          : "页标题背景色",
        "navigationButtonName"              : "导航页按钮",
        "navigationButtonDisplayName"       : "导航按钮显示",
        "navigationButtonTextColorYes"      : "导航按钮文本颜色-有权限",
        "navigationButtonTextColorNo"       : "导航按钮文本颜色-无权限",
        "navigationButtonBackgroundColorYes": "导航按钮背景颜色-有权限",
        "navigationButtonBackgroundColorNo" : "导航按钮背景颜色-无权限",
        "navigationButtonTooltipYes"        : "导航按钮提示-有权限",
        "navigationButtonTooltipNo"         : "导航按钮提示-无权限",
        "note"                              : "备注"
}

DISPLAYOPTON = {1: "1=>调整到页面大小",
                2: "2=>适应宽度",
                3: "3=>实际大小"}

VERTICAL_ALIGNMENT = {"'Middle'": "'Middle'=>垂直对齐：中",
                      "'Top'"   : "'Top'=>垂直对齐：上"}

VISIBILITY = {0: "0=>显示",
              1: "1=>隐藏"}

PERMISSION_INIT_COLUMN_NAME = {
        "name"       : "rls名称",
        "tableColumn": "字段名称",
        "value"      : "字段值"
}

PERMISSION_EDIT_COLUMN_NAME = {
        "userID"           : "用户id",
        "userName"         : "用户名称",
        "userPrincipalName": "PowerBI帐号"
}

PERMISSION_EDIT_COLUMN_NAME_ROLE = {
        "permissionName": "权限名称",
        "dimension"     : "维度",
        "value"         : "维度值列表"
}