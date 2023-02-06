# -*- encoding: utf-8 -*-
"""
@File           :   column_value_en.py
@Time           :   2022-11-10, 周四, 21:43
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   英文 column_value
"""

REPORT_VISUAL_COLUMN_NAME = {
        "name"         : "*name",
        "value"        : "*value",
        "dataCategory" : "*dataCategory",
        # "measureTable": "*measureTable",
        "status"       : "*status",
        "description"  : "description",
        "displayFolder": "displayFolder"
}

DATA_CATEGORY = {
        "RefreshTime"    : "RefreshTime",
        "Uncategorized"  : "Uncategorized",
        "Address"        : "Address",
        "Place"          : "Place",
        "City"           : "City",
        "County"         : "County",
        "StateOrProvince": "State or Province",
        "PostalCode"     : "PostalCode",
        "Country"        : "Country",
        "Continent"      : "Continent",
        "Latitude"       : "Latitude",
        "Longitude"      : "Longitude",
        "WebUrl"         : "WebUrl",
        "ImageUrl"       : "ImageUrl",
        "Barcode"        : "Barcode"
}

COLUMN_NAME = {
        "ordinal"                           : "ordinal",
        "name"                              : "urlName",
        "displayName"                       : "displayName",
        "displayOption"                     : "displayOption",
        "height"                            : "height",
        "width"                             : "width",
        "verticalAlignment"                 : "verticalAlignment",
        "visibility"                        : "visibility",
        "pageTitleText"                     : "pageTitleText",
        "pageTitleTextColor"                : "pageTitleTextColor",
        "pageTitleBackgroundColor"          : "pageTitleBackgroundColor",
        "navigationButtonName"              : "navigationButtonName",
        "navigationButtonDisplayName"       : "navigationButtonDisplayName",
        "navigationButtonTextColorYes"      : "navigationButtonTextColorYes",
        "navigationButtonTextColorNo"       : "navigationButtonTextColorNo",
        "navigationButtonBackgroundColorYes": "navigationButtonBackgroundColorYes",
        "navigationButtonBackgroundColorNo" : "navigationButtonBackgroundColorNo",
        "navigationButtonTooltipYes"        : "navigationButtonTooltipYes",
        "navigationButtonTooltipNo"         : "navigationButtonTooltipNo",
        "note"                              : "note"
}

DISPLAYOPTON = {1: "1=>Fit to page",
                2: "2=>Fit to width",
                3: "3=>Actual size"}

VERTICAL_ALIGNMENT = {"'Middle'": "'Middle'=>Vertical alignment:Middle",
                      "'Top'"   : "'Top'=>Vertical alignment:Top"}

VISIBILITY = {0: "0=>Show Page",
              1: "1=>Hide Page"}

PERMISSION_INIT_COLUMN_NAME = {
        "name"       : "name of rls",
        "tableColumn": "Field name",
        "value"      : "Field value"
}

PERMISSION_EDIT_COLUMN_NAME = {
        "userID"           : "userID",
        "userName"         : "userName",
        "userPrincipalName": "userPrincipalName"
}

PERMISSION_EDIT_COLUMN_NAME_ROLE = {
        "permissionName": "permissionName",
        "dimension"     : "dimension",
        "value"         : "value"
}