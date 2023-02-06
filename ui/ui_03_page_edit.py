# -*- encoding: utf-8 -*-
"""
@File           :   ui_03_page_edit.py
@Time           :   2022-11-10, 周四, 17:40
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   页面编辑
"""

from PySide6.QtCore import Qt
from PySide6.QtGui import QCursor
from PySide6.QtWidgets import (QFileDialog, QHBoxLayout, QLabel, QLineEdit, QMenu, QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget)

from configfiles import COLUMN_NAME, DISPLAYOPTON, DISPLAY_CONFIG, VERTICAL_ALIGNMENT, VISIBILITY
from ui.method import UiMethod, ValidatedItemDelegate
from utils.threads_jpz import ThreadLoadJsonReportPage, ThreadSaveJsonReportPage
from utils.validators import Validator


class PageEdit(UiMethod):
    """页面编辑
    """
    ui_03 = "ui_03"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_page_edit(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_03_dic = DISPLAY_CONFIG[self.ui_03]
        for key in self.ui_03_dic:
            setattr(self, key, key)

        # ui_03布局
        self.widget = QWidget()
        self.layout = QVBoxLayout()
        # 添加子布局
        self.layout.addLayout(self.init_layout_ui_03_1_Lineedit())
        self.layout.addLayout(self.init_layout_ui_03_2_table())
        # self.layout.addStretch()  # 增加弹簧
        self.layout.addLayout(self.init_layout_ui_03_3_label())
        self.widget.setLayout(self.layout)
        self.progress_bar_display()
        return self.widget

    def init_layout_ui_03_1_Lineedit(self):
        """第 1 行

        :return: le_layout
        :rtype: QHBoxLayout
        """
        le_layout = self.Line_edit_layout_x(self.ui_03, self.QLabel_config, self.QLineEdit_config)
        btn1 = self.button_x(self.ui_03, self.QPushButton_choose)
        btn2 = self.button_x(self.ui_03, self.QPushButton_save)
        btn1.clicked.connect(self.ui_03_QPushButton_choose_clicked)
        btn2.clicked.connect(self.ui_03_QPushButton_save_clicked)
        le_layout.addWidget(btn1)
        le_layout.addWidget(btn2)
        return le_layout

    def ui_03_QPushButton_choose_clicked(self):
        """第 1 行,选择按钮槽函数

        :return:None
        :rtype:None
        """
        # 选择 json 文件
        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.json")
        le = self.findChild(QLineEdit, f"{self.ui_03}_{self.QLineEdit_config}")
        le.setText(files)

        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(files)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_03_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0401", msg)
            le.setFocus()
            return
        table = self.findChild(QTableWidget, f"{self.ui_03}_{self.QTableWidget_table}")
        self.thread = ThreadLoadJsonReportPage(files, table)
        self.thread.status.connect(self.load_json_report_page_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_choose}", False)
            self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_save}", False)

    def load_json_report_page_callback(self, status):
        """加载页面的回调函数

        :param status: 0 或 1
        :type status: int
        :return: None
        :rtype: None
        """
        if not status:
            le = self.findChild(QLineEdit, f"{self.ui_03}_{self.QLineEdit_config}")
            le.setText("")
            self.msg_display("msg0402")
            le.setFocus()
        self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_choose}", True)
        self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_save}", True)

    def ui_03_QPushButton_save_clicked(self):
        """第 1 行,保存按钮槽函数

        :return:None
        :rtype:None
        """
        le = self.findChild(QLineEdit, f"{self.ui_03}_{self.QLineEdit_config}")
        le_str = le.text()
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_03_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0403", msg)
            le.setFocus()
            return
        table = self.findChild(QTableWidget, f"{self.ui_03}_{self.QTableWidget_table}")
        self.thread = ThreadSaveJsonReportPage(le_str, table)
        self.thread.status.connect(self.save_json_report_page_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_choose}", False)
            self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_save}", False)

    def save_json_report_page_callback(self, status):
        """保存json文件的回调函数

        :param status: 0 或 1 {"status": 0, "row": row + 1, "col": col + 1, "error": "blank"}
        :type status: dict
        :return: None
        :rtype: None
        """

        # {"status": 0, "row": row + 1, "col": col + 1, "error": "blank"}
        # {"status": 0, "row_old": row, "row": row + 1, "error": "repeat"}

        if not status["status"] and "error" not in status:
            self.msg_display("msg0405")

        elif "error" in status and status["error"] == "blank":
            msg = {"row": status["row"], "col": status["col"]}
            self.msg_display("msg0407", msg)

        elif "error" in status and status["error"] == "repeat":
            msg = {"row_old": status["row_old"], "row": status["row"], "col": status["col"]}
            self.msg_display("msg0408", msg)

        elif "error" in status and status["error"] == "PermissionError":
            self.msg_display("msg1303")  # 完成提示

        elif status["status"]:
            self.msg_display("msg0404", is_info=True)

        self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_choose}", True)
        self.btn_enabled_status(f"{self.ui_03}_{self.QPushButton_save}", True)

    def init_layout_ui_03_2_table(self):
        """第 2 行 表格

        :return: table_layout
        :rtype: QHBoxLayout
        """
        cols = len(COLUMN_NAME)
        table_layout = QHBoxLayout()
        # table_layout.setObjectName("layout_table")

        table = QTableWidget(0, cols)
        table.setObjectName(f"{self.ui_03}_{self.QTableWidget_table}")
        for id, name in enumerate(COLUMN_NAME):
            item = QTableWidgetItem()
            item.setText(COLUMN_NAME[name])
            table.setHorizontalHeaderItem(id, item)
            self.set_table_column_width_resize_to_contents_interactive(table, id)  # 设置宽度
        # table.verticalHeader().setVisible(False)  # 不显示行号
        # 开启右键
        table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        table.customContextMenuRequested.connect(self.ui_03_table_right_menu)
        # 输入校验
        col_reg_list = [
                {"cols": [1], "reg": "[a-zA-Z0-9_-]*"},  # 字母、数字、下划线、中划线
                {"cols": [3], "reg": "[1-3]"},  # 数字 1 至 3
                {"cols": [4, 5], "reg": r"^([1-9]\d\d\d|[1-9]\d\d|[1-9]\d|[1-9])$"},  # 数字 1 至 9999
                {"cols": [6], "reg": "'Middle'|'Top'"},  # 'Middle' 或 'Top'
                {"cols": [7], "reg": "[0-1]"},  # 数字 0 或 1
                {"cols": [9, 10, 13, 14, 15, 16], "reg": "^#([A-Fa-f0-9]{8}|[A-Fa-f0-9]{6}|)$"}  # 16进制颜色，可以是包括最后两位透明度
        ]
        valid = ValidatedItemDelegate()
        valid.col_reg_list = col_reg_list
        table.setItemDelegate(valid)
        table_layout.addWidget(table)
        return table_layout

    def ui_03_table_right_menu(self):
        """开启右键

        :return: None
        :rtype: None
        """
        le = self.findChild(QLineEdit, f"{self.ui_03}_{self.QLineEdit_config}")
        le_str = le.text()
        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_03_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0406", msg)
            le.setFocus()
            return
        table = self.findChild(QTableWidget, f"{self.ui_03}_{self.QTableWidget_table}")

        if selected_items := table.selectedItems():
            self.ui_03_table_right_menu_action(table, selected_items)

    def ui_03_table_right_menu_action(self, table, selected_items):
        """右键绑定动作

        :param table:表格对象
        :type table:object
        :param selected_items:选择的元素列表
        :type selected_items:list
        :return: None
        :rtype: None
        """
        menu = QMenu(table)
        action_edit = menu.addAction(self.ui_03_dic[self.QAction_edit]['display'])
        action = menu.exec(QCursor.pos())

        if action == action_edit:
            item = selected_items[0]
            row = item.row()
            col = item.column()
            self.ui_03_table_right_action_edit(table, row, col)

    def ui_03_table_right_action_edit(self, table, row, column):
        """右键编辑按钮

        :param table: 表格对象
        :type table: object
        :param row: 行号
        :type row: int
        :param column:列号
        :type column: int
        :return: None
        :rtype: None
        """
        # # print("Row %d and Column %d was clicked" % (row, column))
        # 获取表格原来内容
        old = table.currentItem()

        # 根据原来内容打开单选框
        if column == 3:
            radio_key = int(old.text())
            self.radio_choice(table, row, column, DISPLAYOPTON, radio_key)
        elif column == 6:
            radio_key = old.text()
            self.radio_choice(table, row, column, VERTICAL_ALIGNMENT, radio_key)
        elif column == 7:
            radio_key = int(old.text())
            self.radio_choice(table, row, column, VISIBILITY, radio_key)

    def init_layout_ui_03_3_label(self):
        """第 3 行 底部说明

        :return:None
        :rtype: None
        """

        label_layout = QHBoxLayout()
        label = QLabel(self.ui_03_dic[self.QLabel_description]['display'])  # 输入框
        label.setObjectName(f"{self.ui_03}_{self.QLabel_description}")
        # label.setFixedHeight(64)
        # label.setStyleSheet("border: 1px solid red;")  # 通过边框颜色来查看布局避免错误
        label_layout.addWidget(label)
        return label_layout