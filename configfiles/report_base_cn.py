# -*- encoding: utf-8 -*-
"""
@File           :   report_base_cn.py
@Time           :   2022-11-10, 周四, 21:38
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   中文 REPORT_PAGE_BASE_JSON
"""

REPORT_PAGE_BASE_JSON = {
        "Annotation" : [
                "说明",
                "ordinal: 页面排序从 0 开始。",
                "name: 页面名称，在 Power BI 服务中 url 中的使用。",
                "displayName: Power BI 页面的显示名称。",
                "displayOption: 页面视图 1 调整到页面大小 , 2 适应宽度 , 3 实际大小。",
                "height: 页面高度。",
                "width: 页面宽度。",
                "verticalAlignment: 页面垂直对齐,值分选项为：'Top', 'Middle' ;注意单引号的保留。",
                "visibility: 页面隐藏属性, 0 为不隐藏, 1 隐藏。",
                "pageTitleText: 页面标题文字。",
                "pageTitleTextColor: 页面标题文字颜色,使用16进制颜色,加上透明度,末尾两位00表示完全透明,FF完全不透明。",
                "pageTitleBackgroundColor: 页面标题背景颜色，格式同上。",
                "navigationButtonName: 导航按钮名称（选择窗格中查看）。",
                "navigationButtonDisplayName: 导航按钮页面中显示名称。",
                "navigationButtonTextColorYes: 导航按钮文字颜色-有权限，格式同上。",
                "navigationButtonTextColorNo:  导航按钮文字颜色-无权限，格式同上。",
                "navigationButtonBackgroundColorYes: 导航按钮背景颜色-有权限，格式同上。",
                "navigationButtonBackgroundColorNo:  导航按钮背景颜色-无权限，格式同上。",
                "navigationButtonTooltipYes: 导航按钮鼠标放上去的工具提示-有权限。",
                "navigationButtonTooltipNo:  导航按钮鼠标放上去的工具提示-无权限。",
                "note: 备注说明。"
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
                        "navigationButtonDisplayName"       : "点击开始",
                        "navigationButtonTextColorYes"      : "#1E2F56",
                        "navigationButtonTextColorNo"       : "#AAAAAA",
                        "navigationButtonBackgroundColorYes": "#1E2F5600",
                        "navigationButtonBackgroundColorNo" : "#DDDDDD",
                        "navigationButtonTooltipYes"        : "首页",
                        "navigationButtonTooltipNo"         : "无权限",
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
                        "pageTitleText"                     : "导航",
                        "pageTitleTextColor"                : "#1E2F56FF",
                        "pageTitleBackgroundColor"          : "#1E2F5600",
                        "navigationButtonName"              : "NB_Navigation",
                        "navigationButtonDisplayName"       : "返回导航",
                        "navigationButtonTextColorYes"      : "#1E2F56",
                        "navigationButtonTextColorNo"       : "#AAAAAA",
                        "navigationButtonBackgroundColorYes": "#1E2F56EE",
                        "navigationButtonBackgroundColorNo" : "#DDDDDD",
                        "navigationButtonTooltipYes"        : "导航",
                        "navigationButtonTooltipNo"         : "无权限",
                        "note"                              : "内容页的返回按钮的信息。"
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
                        "pageTitleText"                     : "无权限",
                        "pageTitleTextColor"                : "#1E2F56FF",
                        "pageTitleBackgroundColor"          : "#1E2F5600",
                        "navigationButtonName"              : "NB_NoPermission",
                        "navigationButtonDisplayName"       : "无权限",
                        "navigationButtonTextColorYes"      : "#1E2F56",
                        "navigationButtonTextColorNo"       : "#AAAAAA",
                        "navigationButtonBackgroundColorYes": "#1E2F5600",
                        "navigationButtonBackgroundColorNo" : "#DDDDDD",
                        "navigationButtonTooltipYes"        : "无权限",
                        "navigationButtonTooltipNo"         : "无权限",
                        "note"                              : "无权限页面，自行设计页面内容"
                }
        ]
}