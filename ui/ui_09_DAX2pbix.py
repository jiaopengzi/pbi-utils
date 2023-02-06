# -*- encoding: utf-8 -*-
"""
@File           :   ui_09_DAX2pbix.py
@Time           :   2022-11-10, 周四, 19:27
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   dax 2 pbix
"""

from random import random

from PySide6.QtWidgets import QComboBox, QFileDialog, QHBoxLayout, QLabel, QLineEdit, QVBoxLayout, QWidget

from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod
from utils.pbit import Pbit
from utils.threads_jpz import ThreadExtractPbix
from utils.validators import Validator


class DAX2Pbix(UiMethod):
    """dax 2 pbix
    """

    ui_09 = "ui_09"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_dax_2_pbix(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_09_dic = DISPLAY_CONFIG[self.ui_09]
        for key in self.ui_09_dic:
            setattr(self, key, key)

        self.widget_09 = QWidget()
        self.ui_09_layout = QVBoxLayout()
        # 添加子布局
        self.ui_09_layout.addLayout(self.ui_dax_2_pbix_layout_v1())
        self.widget_09.setLayout(self.ui_09_layout)
        self.progress_bar_display()
        return self.widget_09

    def ui_dax_2_pbix_layout_v1(self):
        """布局 v1

        :return: layout
        :rtype: QVBoxLayout
        """

        layout = QVBoxLayout()
        # 第一行
        Line_edit_layout0 = self.Line_edit_layout_x(self.ui_09, self.QLabel_pbix, self.QLineEdit_pbix)
        btn1 = self.button_x(self.ui_09, self.QPushButton_pbix)
        btn1.clicked.connect(self.ui_09_QPushButton_pbix_clicked)
        Line_edit_layout0.addWidget(btn1)
        layout.addLayout(Line_edit_layout0)

        # 第二行
        layout.addLayout(self.combobox_layout_x(self.ui_09, self.QLabel_measuretable, self.QComboBox_measuretable, minimum_width=400))

        # 第三行
        Line_edit_layout1 = self.Line_edit_layout_x(self.ui_09, self.QLabel_dax, self.QLineEdit_dax)
        btn2 = self.button_x(self.ui_09, self.QPushButton_dax)
        btn2.clicked.connect(self.ui_09_QPushButton_dax_clicked)
        Line_edit_layout1.addWidget(btn2)
        layout.addLayout(Line_edit_layout1)

        # 第四行
        btn3 = self.button_x(self.ui_09, self.QPushButton_load)
        btn3.clicked.connect(self.ui_09_QPushButton_load_clicked)
        btn3_layout = QHBoxLayout()
        btn3_layout.addStretch(1)
        btn3_layout.addWidget(btn3)
        btn3_layout.addStretch(1)
        layout.addLayout(btn3_layout)

        # 第五行
        label = QLabel()
        label.setObjectName(f"{self.ui_09}_{self.QLabel_load}")
        label_layout = QHBoxLayout()
        label_layout.addStretch(1)
        label_layout.addWidget(label)
        label_layout.addStretch(1)
        layout.addLayout(label_layout)
        layout.addStretch(1)
        return layout

    def ui_09_QPushButton_pbix_clicked(self):
        """选择 pbix

        :return: None
        :rtype: None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        le0 = self.findChild(QLineEdit, f"{self.ui_09}_{self.QLineEdit_pbix}")
        label = self.findChild(QLabel, f"{self.ui_09}_{self.QLabel_load}")
        le0.setText(files)

        le0_str = le0.text()
        valid = Validator()
        le1_pbix = valid.is_pbix(le0_str)
        if not le1_pbix[0]:
            msg = {"value": f"{self.ui_09_dic[self.QLabel_pbix]['display']}:{le1_pbix[1]}"}
            self.msg_display("msg1001", msg)
            le0.setFocus()
            return

        self.thread0 = ThreadExtractPbix(le0_str)
        self.thread0.signal_dic.connect(self.extract_dax_2_pbix_callback)
        self.thread0.start()

        # 设置线程运行按钮不可点击
        if self.thread0.isRunning():
            self.btn_dax_2_pbix(False)
            label.setText(self.ui_09_dic[self.QLabel_load]['display'])

    def extract_dax_2_pbix_callback(self, signal_dic):
        """提后的回调函数

        :param signal_dic: 字典中五元素，依次是 线程序号，进度数值，提取的文件夹,状态，错误及错误内容
        :type signal_dic: dict
        :return: None
        :rtype: None
        """
        try:
            if "error" in signal_dic and signal_dic["error"] == "extract_error":
                # , "error": "extract_error"
                self.msg_display("msg1301")  # 错误提示
                self.progress_bar_display()  # 进度条归零
                self.btn_dax_2_pbix(True)
                label = self.findChild(QLabel, f"{self.ui_09}_{self.QLabel_load}")
                label.setText("")
                return
            if "error" in signal_dic and signal_dic["error"] == "PermissionError":
                self.msg_display("msg1303")  # 完成提示
                self.progress_bar_display()  # 进度条归零
                self.btn_dax_2_pbix(True)
                label = self.findChild(QLabel, f"{self.ui_09}_{self.QLabel_load}")
                label.setText("")
                return
            if signal_dic["progress"]:
                combo = self.findChild(QComboBox, f"{self.ui_09}_{self.QComboBox_measuretable}")
                label = self.findChild(QLabel, f"{self.ui_09}_{self.QLabel_load}")
                le0_str = self.findChild(QLineEdit, f"{self.ui_09}_{self.QLineEdit_pbix}").text()
                pbit = Pbit()
                pbit.path_pbix_source = le0_str
                tables = pbit.get_all_tables()
                combo.clear()
                for table in tables:
                    combo.addItem(table)
                self.btn_dax_2_pbix(True)
                label.setText("")
                # 初始化小于 10 的随机的进度
                self.progress_bar_display(int(random() * 50 + 10))
            else:
                self.msg_display("msg1002")
        except PermissionError:
            self.msg_display("msg1303")  # 完成提示

    def ui_09_QPushButton_dax_clicked(self):
        """选择 文件夹

        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        folder = str(file_diglog.getExistingDirectory())
        le2 = self.findChild(QLineEdit, f"{self.ui_09}_{self.QLineEdit_dax}")
        le2.setText(folder)
        le2_str = le2.text()
        valid = Validator()
        le2_isdir = valid.is_dir(le2_str)
        if not le2_isdir[0]:
            msg = {"value": f"{self.ui_09_dic[self.QLabel_dax]['display']}:{le2_isdir[1]}"}
            self.msg_display("msg1003", msg)
            le2.setFocus()
            return

    def ui_09_QPushButton_load_clicked(self):
        """执行

        :return:None
        :rtype: None
        """
        try:

            valid = Validator()

            le1 = self.findChild(QLineEdit, f"{self.ui_09}_{self.QLineEdit_pbix}")
            le1_str = le1.text()
            le1_pbix = valid.is_pbix(le1_str)
            if not le1_pbix[0]:
                msg = {"value": f"{self.ui_09_dic[self.QLabel_pbix]['display']}:{le1_pbix[1]}"}
                self.msg_display("msg1004", msg)
                le1.setFocus()
                return

            le2 = self.findChild(QComboBox, f"{self.ui_09}_{self.QComboBox_measuretable}")
            le2_str = le2.currentText()
            le2_isdir = valid.is_text(le2_str)
            if not le2_isdir[0]:
                msg = {"value": f"{self.ui_09_dic[self.QLabel_measuretable]['display']}:{le2_isdir[1]}"}
                self.msg_display("msg1005", msg)
                le2.setFocus()
                return

            le3 = self.findChild(QLineEdit, f"{self.ui_09}_{self.QLineEdit_dax}")
            le3_str = le3.text()
            le3_isdir = valid.is_dir(le3_str)
            if not le3_isdir[0]:
                msg = {"value": f"{self.ui_09_dic[self.QLabel_dax]['display']}:{le3_isdir[1]}"}
                self.msg_display("msg1006", msg)
                le3.setFocus()
                return
            if not self.has_file_type(le3_str, ".dax"):
                msg = {"value": self.ui_09_dic[self.QLabel_dax]['display']}
                self.msg_display("msg1007", msg)
                le3.setFocus()
                return

            # 导入 dax 并编译为 pbit
            self.progress_bar_display(int(random() * 30 + 60))
            pbit = Pbit()
            pbit.path_pbix_source = le1_str
            pbit.folder_temp = pbit.folder_pbix_extract()
            pbit.measure_import_folder_2_pbix(le2_str, le3_str)
            pbit.path_pbit_target = f"{le1_str[:-1]}t"
            pbit.pbi_tools_command_compile()
            if not pbit.pbi_tools_command_compile_status:
                self.msg_display("msg1302")
                self.progress_bar_display()
                self.btn_dax_2_pbix(True)
                return

            self.progress_bar_display(100)
            msg = {"value": pbit.path_pbit_target}
            self.msg_display("msg1008", msg, is_info=True)
            self.progress_bar_display()
            self.btn_dax_2_pbix(True)
        except Exception:
            self.msg_display("msg1009")

    def btn_dax_2_pbix(self, boolean):
        """按钮状态

        :param boolean: True False
        :type boolean: bool
        :return: None
        :rtype: None
        """
        self.btn_enabled_status(f"{self.ui_09}_{self.QPushButton_pbix}", boolean)
        self.btn_enabled_status(f"{self.ui_09}_{self.QPushButton_dax}", boolean)
        self.btn_enabled_status(f"{self.ui_09}_{self.QPushButton_load}", boolean)