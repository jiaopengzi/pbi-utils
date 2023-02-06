# -*- encoding: utf-8 -*-
"""
@File           :   ui_04_permission_init.py
@Time           :   2022-11-10, 周四, 17:48
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   权限初始化
"""

from PySide6.QtWidgets import (QComboBox, QFileDialog, QHBoxLayout,
                               QLabel, QLineEdit, QPlainTextEdit, QTableWidget,
                               QTableWidgetItem, QVBoxLayout, QWidget)

from configfiles import DISPLAY_CONFIG, PERMISSION_INIT_COLUMN_NAME
from ui.method import UiMethod
from utils.threads_jpz import (ThreadDeleteJsonPermissionList, ThreadLoadJsonPermissionList, ThreadSaveJsonPermissionList)
from utils.validators import Validator


class PermissionInit(UiMethod):
    """权限初始化
    """
    ui_04 = "ui_04"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_permission_init(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_04_dic = DISPLAY_CONFIG[self.ui_04]
        for key in self.ui_04_dic:
            setattr(self, key, key)

        self.widget_04 = QWidget()
        self.ui_04_layout = QVBoxLayout()
        # 添加子布局
        self.ui_04_layout.addLayout(self.ui_permission_init_layout_v1())
        self.ui_04_layout.addLayout(self.ui_permission_init_layout_v2())
        self.ui_04_layout.addLayout(self.ui_permission_init_layout_v3())
        self.widget_04.setLayout(self.ui_04_layout)
        self.progress_bar_display()
        return self.widget_04

    def ui_permission_init_layout_v1(self):
        """布局 v1

        :return: QHBoxLayout
        :rtype: layout
        """
        layout = QHBoxLayout()
        Line_edit_layout0 = self.Line_edit_layout_x(self.ui_04, self.QLabel_config, self.QLineEdit_config)
        btn1 = self.button_x(self.ui_04, self.QPushButton_choose)
        btn1.clicked.connect(self.btn1_clicked_permission_init)
        Line_edit_layout0.addWidget(btn1)
        layout.addLayout(Line_edit_layout0)
        return layout

    def ui_permission_init_layout_v2(self):
        """布局 v2

        :return: ui_permission_init_layout_v2
        :rtype: QHBoxLayout
        """
        ui_permission_init_layout_v2 = QHBoxLayout()
        ui_permission_init_layout_v2.addLayout(self.ui_permission_init_layout_v2_left())
        ui_permission_init_layout_v2.addWidget(self.ui_permission_init_layout_v2_right())
        return ui_permission_init_layout_v2

    def ui_permission_init_layout_v2_left(self):
        """布局 v2 左

        :return: layout
        :rtype:QVBoxLayout
        """

        layout = QVBoxLayout()
        layout.addLayout(self.Line_edit_layout_x(self.ui_04, self.QLabel_rlsname, self.QLineEdit_rlsname,
                                                 minimum_width=400, reg_str="^\w+$"))
        layout.addLayout(self.combobox_layout_x(self.ui_04, self.QLabel_table, self.QComboBox_table,
                                                self.combo3_currentTextChanged_permission_list, minimum_width=400))
        layout.addLayout(self.combobox_layout_x(self.ui_04, self.QLabel_column, self.QComboBox_column, minimum_width=400))

        label5 = QLabel(self.ui_04_dic[self.QLabel_value]['display'])
        label5.setObjectName(f"{self.ui_04}_{self.QLabel_value}")
        label5.setMinimumWidth(80)
        text_edit5 = QPlainTextEdit()
        text_edit5.setObjectName(f"{self.ui_04}_{self.QPlainTextEdit_value}")
        text_edit5.setPlaceholderText(self.ui_04_dic[self.QPlainTextEdit_value]['placeholder'])

        layout.addWidget(label5)
        layout.addWidget(text_edit5)
        return layout

    def ui_permission_init_layout_v2_right(self):
        """布局 v2 右边表格

        :return: table
        :rtype: QTableWidget
        """

        cols = len(PERMISSION_INIT_COLUMN_NAME)
        table = QTableWidget(0, cols)
        table.setObjectName(f"{self.ui_04}_{self.QTableWidget_table}")
        for id, name in enumerate(PERMISSION_INIT_COLUMN_NAME):
            item = QTableWidgetItem()
            item.setText(PERMISSION_INIT_COLUMN_NAME[name])
            table.setHorizontalHeaderItem(id, item)
            self.set_table_column_width_resize_to_contents_interactive(table, id)  # 设置宽度
        table.cellDoubleClicked.connect(self.table_doubleClicked_permissioninit)  # 双击事件
        return table

    def table_doubleClicked_permissioninit(self, row):
        """表格双击行数

        :param row: 行号
        :type row: int
        :return: None
        :rtype:None
        """
        table = self.findChild(QTableWidget, f"{self.ui_04}_{self.QTableWidget_table}")
        le = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_rlsname}")
        combo1 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_table}")
        combo2 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_column}")
        te = self.findChild(QPlainTextEdit, f"{self.ui_04}_{self.QPlainTextEdit_value}")
        # # print("Row %d and Column %d was clicked" % (row, column))
        # 获取表格原来内容
        cols = table.columnCount()
        for col in range(cols):
            text = table.item(row, col).text()
            if col == 0:
                le.setText(text)
            elif col == 1:
                text_combo = text.split("'[")
                combo1.setCurrentText(text_combo[0][1:])
                combo2.setCurrentText(text_combo[1][:-1])
            elif col == 2:
                text_line = text.split(",")
                plain_text = "".join(line + "\n" for line in text_line)
                te.setPlainText(plain_text)

    def ui_permission_init_layout_v3(self):
        """布局 v3

        :return: layout
        :rtype: QHBoxLayout
        """
        layout = QHBoxLayout()

        btn7 = self.button_x(self.ui_04, self.QPushButton_save)
        btn8 = self.button_x(self.ui_04, self.QPushButton_del)
        btn7.clicked.connect(self.btn7_clicked_permission_init)
        btn8.clicked.connect(self.btn8_clicked_permission_init)
        label = QLabel(self.ui_04_dic[self.QLabel_description]['display'])
        layout.addWidget(btn7)
        layout.addWidget(btn8)
        layout.addStretch(1)
        layout.addWidget(label)
        layout.addStretch(1)

        return layout

    def btn1_clicked_permission_init(self):
        """按钮1

        :return: None
        :rtype: None
        """
        try:
            # 判断文件是否正确
            file_diglog = QFileDialog()
            files, _ = file_diglog.getOpenFileName(filter="*.json")
            le = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
            le.setText(files)
            # 加载 json 文件
            # 校验器
            valid = Validator()
            le_is_json = valid.is_json(files)
            if not le_is_json[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
                self.msg_display("msg0501", msg)
                le.setFocus()
                return

            table = self.findChild(QTableWidget, f"{self.ui_04}_{self.QTableWidget_table}")
            self.thread = ThreadLoadJsonPermissionList(files, table)
            self.thread.status.connect(self.load_json_permissionlist_callback)
            self.thread.start()
            # 按钮状态
            if self.thread.isRunning():
                self.btn_permissionlist(False)
        except Exception:
            self.msg_display("msg0502")

    def load_json_permissionlist_callback(self, status):
        """加载 json 回调函数

        :param status:0 或 1 {"status": 1, "file": self.path}
        :type status:dict
        :return:None
        :rtype:None
        """
        if not status["status"]:
            le = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
            le.setText("")
            self.msg_display("msg0503")
            le.setFocus()
            self.btn_permissionlist(True)
            return
        # 读取下拉框数据
        self.combobox_update(f"{self.ui_04}_{self.QComboBox_table}", status["file"], "TableColumns")
        # 标题宽度设置
        table = self.findChild(QTableWidget, f"{self.ui_04}_{self.QTableWidget_table}")
        # print("table")
        for id in range(table.columnCount()):
            self.set_table_column_width_resize_to_contents_interactive(table, id)
        self.btn_permissionlist(True)

    def btn_permissionlist(self, boolean):
        """按钮状态

        :param boolean:True False
        :type boolean:bool
        :return:None
        :rtype:None
        """
        self.btn_enabled_status(f"{self.ui_04}_{self.QPushButton_choose}", boolean)
        self.btn_enabled_status(f"{self.ui_04}_{self.QPushButton_save}", boolean)
        self.btn_enabled_status(f"{self.ui_04}_{self.QPushButton_del}", boolean)

    def btn7_clicked_permission_init(self):
        """保存按钮

        :return:None
        :rtype:None
        """
        try:

            # 获取控件信息
            le0 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
            le2 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_rlsname}")
            combo1 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_table}")
            combo2 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_column}")
            te = self.findChild(QPlainTextEdit, f"{self.ui_04}_{self.QPlainTextEdit_value}")
            table = self.findChild(QTableWidget, f"{self.ui_04}_{self.QTableWidget_table}")

            # 获取控件文本
            le0_str = le0.text()
            le2_str = le2.text()
            combo1_str = combo1.currentText()
            combo2_str = combo2.currentText()
            te_str = te.toPlainText()

            valid = Validator()

            le0_is_json = valid.is_json(le0_str)
            le2_istext = valid.is_text(le2_str)
            combo1_istext = valid.is_text(combo1_str)
            combo2_istext = valid.is_text(combo2_str)
            te_istext = valid.is_text(te_str)

            # 点击前验证
            if not le0_is_json[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_config]['display']}:{le0_is_json[1]}"}
                self.msg_display("msg0504", msg)
                le0.setFocus()
                return
            if not le2_istext[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_rlsname]['display']}:{le2_istext[1]}"}
                self.msg_display("msg0505", msg)
                le2.setFocus()
                return
            if not combo1_istext[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_table]['display']}:{combo1_istext[1]}"}
                self.msg_display("msg0506", msg)
                combo1.setFocus()
                return
            if not combo2_istext[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_column]['display']}:{combo2_istext[1]}"}
                self.msg_display("msg0507", msg)
                combo2.setFocus()
                return
            if not te_istext[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_value]['display']}:{te_istext[1]}"}
                self.msg_display("msg0508", msg)
                te.setFocus()
                return
            # rls_name, table_name, column_name, column_value, path, table,
            self.thread = ThreadSaveJsonPermissionList(le2_str, combo1_str, combo2_str, te_str, le0_str, table)
            self.thread.status.connect(self.save_json_permissionlist_callback)
            self.thread.start()
            # 设置线程运行按钮不可点击
            if self.thread.isRunning():
                self.btn_permissionlist(False)
        except Exception:
            self.msg_display("msg0509")

    def save_json_permissionlist_callback(self, status):
        """保存 json 的回调函数

        :param status:0 或 1
        :type status:int
        :return:None
        :rtype:None
        """

        le0 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
        if not status:
            self.msg_display("msg0510")
        self.btn_permissionlist(True)
        self.combobox_update(f"{self.ui_04}_{self.QComboBox_table}", le0.text(), "TableColumns")

    def btn8_clicked_permission_init(self):
        """删除按钮

        :return:None
        :rtype:None
        """
        try:
            # 获取控件信息
            le0 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
            le2 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_rlsname}")
            table = self.findChild(QTableWidget, f"{self.ui_04}_{self.QTableWidget_table}")

            # 获取控件文本
            le0_str = le0.text()
            le2_str = le2.text()

            valid = Validator()

            le0_is_json = valid.is_json(le0_str)
            le2_istext = valid.is_text(le2_str)

            # 点击前验证
            if not le0_is_json[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_config]['display']}:{le0_is_json[1]}"}
                self.msg_display("msg0511", msg)
                le0.setFocus()
                return
            if not le2_istext[0]:
                msg = {"value": f"{self.ui_04_dic[self.QLabel_rlsname]['display']}:{le2_istext[1]}"}
                self.msg_display("msg0512", msg)
                le2.setFocus()
                return

            self.thread = ThreadDeleteJsonPermissionList(le2_str, le0_str, table)
            self.thread.status.connect(self.delete_json_permissionlist_callback)
            self.thread.start()
            # 设置线程运行按钮不可点击
            if self.thread.isRunning():
                self.btn_permissionlist(False)

        except Exception:
            self.msg_display("msg0513")

    def delete_json_permissionlist_callback(self, status):
        """删除 json 的回调函数

        :param status:0 或 1
        :type status:int
        :return:None
        :rtype:None
        """

        if not status:
            self.msg_display("msg0514")

        le0 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
        le2 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_rlsname}")
        combo1 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_table}")
        combo2 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_column}")
        te = self.findChild(QPlainTextEdit, f"{self.ui_04}_{self.QPlainTextEdit_value}")

        le2.clear()
        combo1.clear()
        combo2.clear()
        te.clear()
        self.btn_permissionlist(True)
        self.combobox_update(f"{self.ui_04}_{self.QComboBox_table}", le0.text(), "TableColumns")

    def combo3_currentTextChanged_permission_list(self):
        """下拉框文本变化

        :return:None
        :rtype:None
        """
        le0 = self.findChild(QLineEdit, f"{self.ui_04}_{self.QLineEdit_config}")
        combo1 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_table}")
        combo2 = self.findChild(QComboBox, f"{self.ui_04}_{self.QComboBox_column}")
        key_l2 = combo1.currentText()
        combo2.clear()
        # self.combobox_update("combo_view04_3", files, "TableColumns")
        self.combobox_update(f"{self.ui_04}_{self.QComboBox_column}", le0.text(), "TableColumns", key_l2)
        self.set_text_color(combo1)