# -*- encoding: utf-8 -*-
"""
@File           :   menu_action_en.py
@Time           :   2022-11-10, 周四, 21:40
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   英文 MENU_ACTION_LIST
"""

MENU_ACTION_LIST = {
        "menu_home"               : {"displayname": "start", "ico": "", "parent": "self", "func": ""},
        "action_home"             : {"displayname": "home", "ico": "home.svg", "parent": "menu_home", "func": "ui_home"},

        "menu_tool"               : {"displayname": "tool", "ico": "", "parent": "self", "func": ""},

        "menu_pbit"               : {"displayname": "Report", "ico": "Report.svg", "parent": "menu_tool", "func": ""},
        "action_config_init"      : {"displayname": "init", "ico": "init.svg", "parent": "menu_pbit", "func": "ui_json_init"},
        "action_reportvisual_edit": {"displayname": "template\nmeasure", "ico": "template.svg", "parent": "menu_pbit", "func": "ui_report_visual_edit"},
        "action_report_page_edit" : {"displayname": "page\nedit", "ico": "edit.svg", "parent": "menu_pbit", "func": "ui_page_edit"},
        "menu_permission"         : {"displayname": "permission\nconfig", "ico": "permission.svg", "parent": "menu_pbit", "func": ""},
        "action_permission_init"  : {"displayname": "permission\ncategory init", "ico": "permission_init.svg", "parent": "menu_permission", "func": "ui_permission_init"},
        "action_permission_edit"  : {"displayname": "permission\ntable edit", "ico": "permission_edit.svg", "parent": "menu_permission", "func": "ui_permission_edit"},
        "action_pbit_compile"     : {"displayname": "create\npbit", "ico": "compile.svg", "parent": "menu_pbit", "func": "ui_pbix2pbit"},
        "menu_measure"            : {"displayname": "measure", "ico": "measure.svg", "parent": "menu_tool", "func": ""},
        "menu_about"              : {"displayname": "about", "ico": "", "parent": "self", "func": ""},
        "action_pbixA_2_pbixB"    : {"displayname": "pbixA 2 pbixB", "ico": "A2B.svg", "parent": "menu_measure", "func": "ui_pbixa_2_pbixb"},
        "action_pbix_2_dax"       : {"displayname": "pbix 2 DAX", "ico": "out.svg", "parent": "menu_measure", "func": "ui_pbix_2_dax"},
        "action_dax_2_pbix"       : {"displayname": "DAX 2 pbix", "ico": "in.svg", "parent": "menu_measure", "func": "ui_dax_2_pbix"},
        "action_about"            : {"displayname": "about", "ico": "about.svg", "parent": "menu_about", "func": "ui_about"}
}