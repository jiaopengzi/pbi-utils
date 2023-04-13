# -*- encoding: utf-8 -*-
"""
@File           :   display_config_cn.py
@Time           :   2022-11-10, 周四, 21:41
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   中文 DISPLAY_CONFIG
"""

DISPLAY_CONFIG = {

        "home"        : {
                "QLabel_home": {"display": "欢迎使用"},
        },
        "about"       : {
                "QTextBrowser_about": {"display": ""},
                "QLabel_language"   : {"display": "语言："},
                "QComboBox_language": {"placeholder": "请选择"}
        },

        "dialog_radio": {
                "Title_display": {"display": "请选择"}
        },

        "dialog_text" : {
                "Title_display"          : {"display": "多行输入"},
                "QPushButton_commit"       : {"display": "提交", "icon": "btn_commit.svg"},
                "QPlainTextEdit_content1": {"placeholder": '示例: 1001|用户1|帐号1|0,1,2,3|值1,值2|值3\n字段间使用 "|" 分隔，字段值间使用英文半角逗号 "," 分隔'},
                "QPlainTextEdit_content2": {"placeholder": '字段值间换行表示\n示例:\n值1\n值2\n值3\n...'},
        },

        "multi_line"  : {
                "Title_display"                : {"display": "多行编辑"},
                "QLineEdit_search"             : {"placeholder": "请输入需要查询的关键字"},
                "QListWidget_left"             : {"placeholder": ""},
                "QPushButton_add"              : {"display": "添加", "icon": "btn_add.svg"},
                "QPushButton_del"              : {"display": "删除", "icon": "btn_del.svg"},
                "QPushButton_left_select_all"  : {"display": "左侧全选", "icon": "btn_left.svg"},
                "QPushButton_cancel_select_all": {"display": "取消全选", "icon": "btn_cancel.svg"},
                "QPushButton_right_clear_all"  : {"display": "右侧清空", "icon": "btn_right.svg"},
                "QLabel_measure_table"         : {"display": "度量值表:"},
                "QComboBox_measure_table"      : {"placeholder": "请选择"},
                "QPushButton_commit"           : {"display": "提交", "icon": "btn_commit.svg"},
                "QListWidget_right"            : {"placeholder": "请选择需要添加的内容"}
        },

        "ui_01"       : {
                "QLabel_pbix"        : {"display": "pbix模板"},
                "QLineEdit_pbix"     : {"placeholder": "例如: D:\\demo\\template.pbix"},
                "QPushButton_choose" : {"display": "选择", "icon": "btn_pbix.png"},
                "QLabel_content"     : {"display": "内容页数"},
                "QLineEdit_content"  : {"placeholder": "例如: 3,5,6 ;请使用英文半角逗号分隔！"},
                "QLabel_config"      : {"display": "配置文件"},
                "QLineEdit_config"   : {"placeholder": "例如: D:\\demo\\source\\config\\config.json"},
                "QPushButton_save"   : {"display": "保存", "icon": "btn_json.svg"},
                "QCheckBox_isencrypt": {"display": "页面 URL 名称是否使用随机值"},
                "QCheckBox_isHidden": {"display": "模板度量值是否隐藏"},
                "QPushButton_init"   : {"display": "初始化", "icon": "init.svg"}
        },

        "ui_02"       : {
                "QLabel_config"     : {"display": "配置文件"},
                "QLineEdit_config"  : {"placeholder": "例如: D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose": {"display": "选择", "icon": "btn_json.svg"},
                "QPushButton_add"   : {"display": "新增", "icon": "btn_add.svg"},
                "QPushButton_save"  : {"display": "保存", "icon": "btn_save.svg"},
                "QTableWidget_table": {},
                "QAction_del"       : {"display": "删除"},
                "QAction_edit"      : {"display": "编辑"},
                "QAction_multi"     : {"display": "多行输入"},
                "QLabel_description": {"display": "字段带星号(*)为必填"}
        },

        "ui_03"       : {
                "QLabel_config"     : {"display": "配置文件"},
                "QLineEdit_config"  : {"placeholder": "例如: D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose": {"display": "选择", "icon": "btn_json.svg"},
                "QPushButton_save"  : {"display": "保存", "icon": "btn_save.svg"},
                "QTableWidget_table": {},
                "QAction_edit"      : {"display": "编辑"},
                "QLabel_description": {"display": "说明\n1、页面视图：1=>调整到页面大小， 2=>适应宽度， 3=>实际大小；\n2、隐藏页：0=>显示， 1=>隐藏；\n3、垂直对齐：'Middle'=>中， 'Top'=>上。注意单引号需要保留。"}
        },

        "ui_04"       : {
                "QLabel_config"       : {"display": "配置文件"},
                "QLineEdit_config"    : {"placeholder": "例如: D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose"  : {"display": "选择", "icon": "btn_json.svg"},
                "QLabel_rlsname"      : {"display": "rls名称"},
                "QLineEdit_rlsname"   : {"placeholder": "例如: rls_1，字母下划线数字组合，且不与度量值重名！"},
                "QLabel_table"        : {"display": "表格"},
                "QComboBox_table"     : {"placeholder": "例如: 表1"},
                "QLabel_column"       : {"display": "字段"},
                "QComboBox_column"    : {"placeholder": "例如: 字段1"},
                "QLabel_value"        : {"display": "字段值"},
                "QPlainTextEdit_value": {"placeholder": "多值请换行"},
                "QPushButton_save"    : {"display": "保存", "icon": "btn_save.svg"},
                "QPushButton_del"     : {"display": "删除", "icon": "btn_del.svg"},
                "QTableWidget_table"  : {},
                "QLabel_description"  : {"display": "双击表格单元格，可以选中对应行。"}
        },

        "ui_05"       : {
                "QLabel_config"     : {"display": "配置文件"},
                "QLineEdit_config"  : {"placeholder": "例如: D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose": {"display": "选择", "icon": "btn_json.svg"},
                "QPushButton_add"   : {"display": "新增", "icon": "btn_add.svg"},
                "QPushButton_save"  : {"display": "保存", "icon": "btn_save.svg"},
                "QTableWidget_table": {},
                "QAction_del"       : {"display": "删除"},
                "QAction_edit"      : {"display": "编辑"},
                "QAction_init"      : {"display": "行初始化"},
                "QAction_multi"     : {"display": "多行输入"}
        },

        "ui_06"       : {
                "QLabel_pbix"            : {"display": "pbix模板"},
                "QLineEdit_pbix"         : {"placeholder": "例如: D:\\demo\\template.pbix"},
                "QPushButton_pbix"       : {"display": "选择", "icon": "btn_pbix.png"},
                "QLabel_config"          : {"display": "配置文件"},
                "QLineEdit_config"       : {"placeholder": "例如: D:\\demo\\source\\config\\config.json"},
                "QPushButton_choose"     : {"display": "选择", "icon": "btn_json.svg"},
                "QLabel_pbit"            : {"display": "pbit文件"},
                "QLineEdit_pbit"         : {"placeholder": "例如: D:\\demo\\custom.pbit"},
                "QPushButton_pbit"       : {"display": "选择", "icon": "btn_pbit.png"},
                "QLabel_measuretable"    : {"display": "度量值表"},
                "QComboBox_measuretable" : {"placeholder": "若不能选择，请初始化配置文件！"},
                "QLabel_measurefolder"   : {"display": "度量值文件夹"},
                "QLineEdit_measurefolder": {"placeholder": "例如: template\\navigation"},
                "QPushButton_create"     : {"display": "生成pbit", "icon": "btn_create.svg"}
        },

        "ui_07"       : {
                "QLabel_pbixA"     : {"display": "pbixA"},
                "QLineEdit_pbixA"  : {"placeholder": "例如: D:\\demo\\customA.pbix"},
                "QPushButton_pbixA": {"display": "选择", "icon": "btn_pbix.png"},
                "QLabel_pbixB"     : {"display": "pbixB"},
                "QLineEdit_pbixB"  : {"placeholder": "例如: D:\\demo\\customB.pbix"},
                "QPushButton_pbixB": {"display": "选择", "icon": "btn_pbix.png"},
                "QPushButton_load" : {"display": "加载数据", "icon": "btn_load.svg"}
        },

        "ui_08"       : {
                "QLabel_pbix"     : {"display": "pbix"},
                "QLineEdit_pbix"  : {"placeholder": "例如: D:\\demo\\custom.pbix"},
                "QPushButton_pbix": {"display": "选择", "icon": "btn_pbix.png"},
                "QLabel_dax"      : {"display": "dax文件夹"},
                "QLineEdit_dax"   : {"placeholder": "例如: D:\\demo\\dax"},
                "QPushButton_dax" : {"display": "选择", "icon": "btn_folder.svg"},
                "QPushButton_load": {"display": "导出DAX", "icon": "out.svg"}
        },

        "ui_09"       : {
                "QLabel_pbix"           : {"display": "pbix"},
                "QLineEdit_pbix"        : {"placeholder": "例如: D:\\demo\\custom.pbix"},
                "QPushButton_pbix"      : {"display": "读取", "icon": "btn_pbix.png"},
                "QLabel_measuretable"   : {"display": "度量值表"},
                "QComboBox_measuretable": {"placeholder": "若不能选择，请重新读取！"},
                "QLabel_dax"            : {"display": "dax文件夹"},
                "QLineEdit_dax"         : {"placeholder": "例如: D:\\demo\\dax"},
                "QPushButton_dax"       : {"display": "选择", "icon": "btn_folder.svg"},
                "QPushButton_load"      : {"display": "导入DAX", "icon": "in.svg"},
                "QLabel_load"           : {"display": "正在读取，请稍候..."},
        },
        "ui_main"     : {
                "QStatusBar_status"  : {"display": "状态栏"},
                "QLabel_status"      : {"display": "状态栏标签"},
                "QProgressBar_status": {"display": "进度条"}
        }
}