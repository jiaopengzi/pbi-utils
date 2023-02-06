# -*- encoding: utf-8 -*-
"""
@File           :   report_base_en.py
@Time           :   2022-11-10, 周四, 21:38
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   英文 REPORT_PAGE_BASE_JSON
"""

REPORT_PAGE_BASE_JSON = {
        "Annotation" : [
                "description",
                "ordinal: Page sorting starts at 0.",
                "name: The name of the page, as used in the URL in the Power BI service.",
                "displayName: The display name of the Power BI page.",
                "displayOption: 1、Page view:1=>Fit to page, 2=>Fit to width, 3=>Actual size;",
                "height: Page height",
                "width: Page width",
                "verticalAlignment: Vertical alignment:'Middle'=>Middle, 'Top'=>Top.Note that single quotes need to be preserved.",
                "visibility: Hide page:0=>show, 1=>hide;",
                "pageTitleText: page title",
                "pageTitleTextColor: Page title text color, using decimal color, plus transparency, with two 00s at the end indicating full transparency and FF being completely opaque.",
                "pageTitleBackgroundColor: Page title background color, same format as above.",
                "navigationButtonName: The navigation button name (selection View in the show pane).",
                "navigationButtonDisplayName: The name of the navigation button page is displayed.",
                "navigationButtonTextColorYes: Navigation button text color - permissioned, format as above.",
                "navigationButtonTextColorNo:  Navigation button text color - no permission, same format as above.",
                "navigationButtonBackgroundColorYes: Navigation button background color - with permission, the format is the same as above",
                "navigationButtonBackgroundColorNo:  Navigation button background color - no permission, same format as above.",
                "navigationButtonTooltipYes: Navigation button mouse on the tooltip - there are permissions.",
                "navigationButtonTooltipNo:  Navigation button mouse on the tooltip - no permission.",
                "note: note"
        ],
        "ReportPages": [
                {
                        "ordinal"                           : 0,
                        "name"                              : "Home",
                        "displayName"                       : "Home",
                        "displayOption"                     : 3,
                        "height"                            : 792,
                        "width"                             : 1280,
                        "verticalAlignment"                 : "'Middle'",
                        "visibility"                        : 0,
                        "pageTitleText"                     : "Power BI Template",
                        "pageTitleTextColor"                : "#1E2F56FF",
                        "pageTitleBackgroundColor"          : "#1E2F5600",
                        "navigationButtonName"              : "NB_Home",
                        "navigationButtonDisplayName"       : "START",
                        "navigationButtonTextColorYes"      : "#1E2F56",
                        "navigationButtonTextColorNo"       : "#AAAAAA",
                        "navigationButtonBackgroundColorYes": "#1E2F5600",
                        "navigationButtonBackgroundColorNo" : "#DDDDDD",
                        "navigationButtonTooltipYes"        : "Home",
                        "navigationButtonTooltipNo"         : "No permissions",
                        "note"                              : ""
                },
                {
                        "ordinal"                           : 1,
                        "name"                              : "Navigation",
                        "displayName"                       : "Navigation",
                        "displayOption"                     : 3,
                        "height"                            : 792,
                        "width"                             : 1280,
                        "verticalAlignment"                 : "'Middle'",
                        "visibility"                        : 1,
                        "pageTitleText"                     : "navigation",
                        "pageTitleTextColor"                : "#1E2F56FF",
                        "pageTitleBackgroundColor"          : "#1E2F5600",
                        "navigationButtonName"              : "NB_Navigation",
                        "navigationButtonDisplayName"       : "Return to navigation",
                        "navigationButtonTextColorYes"      : "#1E2F56",
                        "navigationButtonTextColorNo"       : "#AAAAAA",
                        "navigationButtonBackgroundColorYes": "#1E2F56EE",
                        "navigationButtonBackgroundColorNo" : "#DDDDDD",
                        "navigationButtonTooltipYes"        : "navigation",
                        "navigationButtonTooltipNo"         : "No permissions",
                        "note"                              : "Information about the back button for the content page."
                },
                {
                        "ordinal"                           : 2,
                        "name"                              : "NoPermission",
                        "displayName"                       : "NoPermission",
                        "displayOption"                     : 3,
                        "height"                            : 792,
                        "width"                             : 1280,
                        "verticalAlignment"                 : "'Middle'",
                        "visibility"                        : 1,
                        "pageTitleText"                     : "No permissions",
                        "pageTitleTextColor"                : "#1E2F56FF",
                        "pageTitleBackgroundColor"          : "#1E2F5600",
                        "navigationButtonName"              : "NB_NoPermission",
                        "navigationButtonDisplayName"       : "No permissions",
                        "navigationButtonTextColorYes"      : "#1E2F56",
                        "navigationButtonTextColorNo"       : "#AAAAAA",
                        "navigationButtonBackgroundColorYes": "#1E2F5600",
                        "navigationButtonBackgroundColorNo" : "#DDDDDD",
                        "navigationButtonTooltipYes"        : "No permissions",
                        "navigationButtonTooltipNo"         : "No permissions",
                        "note"                              : "If you do not have permission to the page, design the page content by yourself"
                }
        ]
}