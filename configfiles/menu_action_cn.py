# -*- encoding: utf-8 -*-
"""
@File           :   menu_action_en.py
@Time           :   2022-11-10, 周四, 21:40
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   中文 MENU_ACTION_LIST
"""
MENU_ACTION_LIST = {
        "menu_home"               : {"displayname": "开始", "ico": "", "parent": "self", "func": ""},
        "action_home"             : {"displayname": "首页", "ico": "home.svg", "parent": "menu_home", "func": "ui_home"},

        "menu_tool"               : {"displayname": "工具", "ico": "", "parent": "self", "func": ""},

        "menu_pbit"               : {"displayname": "报告", "ico": "Report.svg", "parent": "menu_tool", "func": ""},
        "action_config_init"      : {"displayname": "初始化", "ico": "init.svg", "parent": "menu_pbit", "func": "ui_json_init"},
        "action_reportvisual_edit": {"displayname": "模板度量值", "ico": "template.svg", "parent": "menu_pbit", "func": "ui_report_visual_edit"},
        "action_report_page_edit" : {"displayname": "页面编辑", "ico": "edit.svg", "parent": "menu_pbit", "func": "ui_page_edit"},
        "menu_permission"         : {"displayname": "权限配置", "ico": "permission.svg", "parent": "menu_pbit", "func": ""},
        "action_permission_init"  : {"displayname": "权限类别初始化", "ico": "permission_init.svg", "parent": "menu_permission", "func": "ui_permission_init"},
        "action_permission_edit"  : {"displayname": "权限表编辑", "ico": "permission_edit.svg", "parent": "menu_permission", "func": "ui_permission_edit"},
        "action_pbit_compile"     : {"displayname": "编译生成pbit", "ico": "compile.svg", "parent": "menu_pbit", "func": "ui_pbix2pbit"},
        "menu_measure"            : {"displayname": "度量值", "ico": "measure.svg", "parent": "menu_tool", "func": ""},
        "menu_about"              : {"displayname": "关于", "ico": "", "parent": "self", "func": ""},
        "action_pbixA_2_pbixB"    : {"displayname": "pbixA 2 pbixB", "ico": "A2B.svg", "parent": "menu_measure", "func": "ui_pbixa_2_pbixb"},
        "action_pbix_2_dax"       : {"displayname": "pbix 2 DAX", "ico": "out.svg", "parent": "menu_measure", "func": "ui_pbix_2_dax"},
        "action_dax_2_pbix"       : {"displayname": "DAX 2 pbix", "ico": "in.svg", "parent": "menu_measure", "func": "ui_dax_2_pbix"},
        "action_about"            : {"displayname": "关于", "ico": "about.svg", "parent": "menu_about", "func": "ui_about"}
}