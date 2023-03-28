# -*- encoding: utf-8 -*-
"""
@File           :   display_config_en.py
@Time           :   2022-11-10, 周四, 21:41
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   英文 DISPLAY_CONFIG
"""

DISPLAY_CONFIG = {

        "home"        : {
                "QLabel_home": {"display": "Welcome"},
        },
        "about"       : {
                "QTextBrowser_about": {"display": ""},
                "QLabel_language"   : {"display": "language:"},
                "QComboBox_language": {"placeholder": "select"}
        },

        "dialog_radio": {
                "Title_display": {"display": "Please select"}
        },

        "dialog_text" : {
                "Title_display"          : {"display": "Multi-line input"},
                "QPushButton_commit"       : {"display": "commit", "icon": "btn_commit.svg"},
                "QPlainTextEdit_content1": {"placeholder": 'demo: 1001|user1|account1|0,1,2,3|value1,value2|value3\nUse "|" splitting between fields, and use "," splitting between field values'},
                "QPlainTextEdit_content2": {"placeholder": 'Field values are represented by line breaks\ndemo:\nvalue1\nvalue2\nvalue3\n...'},
        },

        "multi_line"  : {
                "Title_display"                : {"display": "Multi-line editing"},
                "QLineEdit_search"             : {"placeholder": "Please enter the keyword you want to query"},
                "QListWidget_left"             : {"placeholder": ""},
                "QPushButton_add"              : {"display": "add", "icon": "btn_add.svg"},
                "QPushButton_del"              : {"display": "del", "icon": "btn_del.svg"},
                "QPushButton_left_select_all"  : {"display": "all left", "icon": "btn_left.svg"},
                "QPushButton_cancel_select_all": {"display": "deselect all", "icon": "btn_cancel.svg"},
                "QPushButton_right_clear_all"  : {"display": "clear right", "icon": "btn_right.svg"},
                "QLabel_measure_table"         : {"display": "measure table:"},
                "QComboBox_measure_table"      : {"placeholder": "please select"},
                "QPushButton_commit"           : {"display": "commit", "icon": "btn_commit.svg"},
                "QListWidget_right"            : {"placeholder": "Please select what you want to add"}
        },

        "ui_01"       : {
                "QLabel_pbix"        : {"display": "pbix-template"},
                "QLineEdit_pbix"     : {"placeholder": "e.g. D:\\demo\\template.pbix"},
                "QPushButton_choose" : {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_content"     : {"display": "content page list"},
                "QLineEdit_content"  : {"placeholder": "e.g. 3,5,6 ;Please use comma separation!"},
                "QLabel_config"      : {"display": "configuration file"},
                "QLineEdit_config"   : {"placeholder": "e.g. D:\\demo\\source\\config\\config.json"},
                "QPushButton_save"   : {"display": "save", "icon": "btn_save.svg"},
                "QCheckBox_isencrypt": {"display": "Whether the page URL name uses a random value"},
                "QCheckBox_isHidden": {"display": "Whether the template measure is hidden"},
                "QPushButton_init"   : {"display": "init", "icon": "init.svg"}
        },

        "ui_02"       : {
                "QLabel_config"     : {"display": "configuration file"},
                "QLineEdit_config"  : {"placeholder": "e.g. D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose": {"display": "select", "icon": "btn_choose.svg"},
                "QPushButton_add"   : {"display": "add", "icon": "btn_add.svg"},
                "QPushButton_save"  : {"display": "save", "icon": "btn_save.svg"},
                "QTableWidget_table": {},
                "QAction_del"       : {"display": "Del"},
                "QAction_edit"      : {"display": "Edit"},
                "QAction_multi"     : {"display": "Multi-line input"},
                "QLabel_description": {"display": "Fields marked with an asterisk (*) are required"}
        },

        "ui_03"       : {
                "QLabel_config"     : {"display": "configuration file"},
                "QLineEdit_config"  : {"placeholder": "e.g. D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose": {"display": "select", "icon": "btn_choose.svg"},
                "QPushButton_save"  : {"display": "save", "icon": "btn_save.svg"},
                "QTableWidget_table": {},
                "QAction_edit"      : {"display": "Edit"},
                "QLabel_description": {
                        "display": "Description\n1.Page view:1=>Fit to page, 2=>Fit to width, 3=>Actual size;\n2.Hide page:0=>show, 1=>hide;\n3.Vertical alignment:'Middle'=>Middle, 'Top'=>Top.Note that single quotes need to be preserved."}
        },

        "ui_04"       : {
                "QLabel_config"       : {"display": "configuration file"},
                "QLineEdit_config"    : {"placeholder": "e.g. D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose"  : {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_rlsname"      : {"display": "name of rls"},
                "QLineEdit_rlsname"   : {"placeholder": "e.g. rls_1, a combination of letters underscored numbers."},
                "QLabel_table"        : {"display": "table"},
                "QComboBox_table"     : {"placeholder": "e.g. table1"},
                "QLabel_column"       : {"display": "column"},
                "QComboBox_column"    : {"placeholder": "e.g. column1"},
                "QLabel_value"        : {"display": "value"},
                "QPlainTextEdit_value": {"placeholder": "Multi-value line break"},
                "QPushButton_save"    : {"display": "save", "icon": "btn_save.svg"},
                "QPushButton_del"     : {"display": "del", "icon": "btn_del.svg"},
                "QTableWidget_table"  : {},
                "QLabel_description"  : {"display": "Double-click a table cell to select the corresponding row."}
        },

        "ui_05"       : {
                "QLabel_config"     : {"display": "configuration file"},
                "QLineEdit_config"  : {"placeholder": "e.g. D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose": {"display": "select", "icon": "btn_choose.svg"},
                "QPushButton_add"   : {"display": "add", "icon": "btn_add.svg"},
                "QPushButton_save"  : {"display": "save", "icon": "btn_save.svg"},
                "QTableWidget_table": {},
                "QAction_del"       : {"display": "Del"},
                "QAction_edit"      : {"display": "Edit"},
                "QAction_init"      : {"display": "Row Init"},
                "QAction_multi"     : {"display": "Multi-line input"}
        },

        "ui_06"       : {
                "QLabel_pbix"            : {"display": "pbix-template"},
                "QLineEdit_pbix"         : {"placeholder": "e.g. D:\\demo\\template.pbix"},
                "QPushButton_pbix"       : {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_config"          : {"display": "configuration file"},
                "QLineEdit_config"       : {"placeholder": "e.g. D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose"     : {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_pbit"            : {"display": "pbit file"},
                "QLineEdit_pbit"         : {"placeholder": "e.g. D:\\demo\\custom.pbit"},
                "QPushButton_pbit"       : {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_measuretable"    : {"display": "measure table"},
                "QComboBox_measuretable" : {"placeholder": "If you can't choose, initialize the configuration file!"},
                "QLabel_measurefolder"   : {"display": "display folder"},
                "QLineEdit_measurefolder": {"placeholder": "e.g. template\\navigation"},
                "QPushButton_create"     : {"display": "create pbit", "icon": "btn_create.svg"}
        },

        "ui_07"       : {
                "QLabel_pbixA"     : {"display": "pbixA"},
                "QLineEdit_pbixA"  : {"placeholder": "e.g. D:\\demo\\customA.pbix"},
                "QPushButton_pbixA": {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_pbixB"     : {"display": "pbixB"},
                "QLineEdit_pbixB"  : {"placeholder": "e.g. D:\\demo\\customB.pbix"},
                "QPushButton_pbixB": {"display": "select", "icon": "btn_choose.svg"},
                "QPushButton_load" : {"display": "load", "icon": "btn_load.svg"}
        },

        "ui_08"       : {
                "QLabel_pbix"     : {"display": "pbix"},
                "QLineEdit_pbix"  : {"placeholder": "e.g. D:\\demo\\custom.pbix"},
                "QPushButton_pbix": {"display": "select", "icon": "btn_choose.svg"},
                "QLabel_dax"      : {"display": "dax folder"},
                "QLineEdit_dax"   : {"placeholder": "e.g. D:\\demo\\dax"},
                "QPushButton_dax" : {"display": "select", "icon": "btn_choose.svg"},
                "QPushButton_load": {"display": "export DAX", "icon": "out.svg"}
        },

        "ui_09"       : {
                "QLabel_pbix"           : {"display": "pbix"},
                "QLineEdit_pbix"        : {"placeholder": "e.g. D:\\demo\\custom.pbix"},
                "QPushButton_pbix"      : {"display": "read", "icon": "btn_read.svg"},
                "QLabel_measuretable"   : {"display": "measure table"},
                "QComboBox_measuretable": {"placeholder": "If you can't choose, Please re-read!"},
                "QLabel_dax"            : {"display": "dax folder"},
                "QLineEdit_dax"         : {"placeholder": "e.g. D:\\demo\\dax"},
                "QPushButton_dax"       : {"display": "select", "icon": "btn_choose.svg"},
                "QPushButton_load"      : {"display": "import DAX", "icon": "in.svg"},
                "QLabel_load"           : {"display": "reading, please wait..."},
        },
        "ui_main"     : {
                "QStatusBar_status"  : {"display": "Status bar"},
                "QLabel_status"      : {"display": "Status bar label"},
                "QProgressBar_status": {"display": "Progress bar"}
        }
}