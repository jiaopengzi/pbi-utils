# -*- encoding: utf-8 -*-
"""
@File           :   ui_08_pbix2DAX.py
@Time           :   2022-11-10, 周四, 19:21
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   pbix 2 dax
"""

from random import random

from PySide6.QtWidgets import QFileDialog, QHBoxLayout, QLineEdit, QVBoxLayout, QWidget

from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod
from utils.pbit import Pbit
from utils.threads_jpz import ThreadExtractPbix
from utils.validators import Validator


class Pbix2DAX(UiMethod):
    """pbix 2 dax
    """

    ui_08 = "ui_08"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_pbix_2_dax(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_08_dic = DISPLAY_CONFIG[self.ui_08]
        for key in self.ui_08_dic:
            setattr(self, key, key)

        self.widget = QWidget()
        self.layout = QVBoxLayout()
        # 添加子布局
        self.layout.addLayout(self.ui_pbix_2_dax_layout_v1())
        self.widget.setLayout(self.layout)
        self.progress_bar_display()
        return self.widget

    def ui_pbix_2_dax_layout_v1(self):
        """布局 v1

        :return: layout
        :rtype: QVBoxLayout
        """

        layout = QVBoxLayout()
        # 第一行
        Line_edit_layout0 = self.Line_edit_layout_x(self.ui_08, self.QLabel_pbix, self.QLineEdit_pbix)
        btn1 = self.button_x(self.ui_08, self.QPushButton_pbix)
        btn1.clicked.connect(self.ui_08_QPushButton_pbix_clicked)
        Line_edit_layout0.addWidget(btn1)
        layout.addLayout(Line_edit_layout0)

        # 第二行
        Line_edit_layout1 = self.Line_edit_layout_x(self.ui_08, self.QLabel_dax, self.QLineEdit_dax)
        btn2 = self.button_x(self.ui_08, self.QPushButton_dax)
        btn2.clicked.connect(self.ui_08_QPushButton_dax_clicked)
        Line_edit_layout1.addWidget(btn2)
        layout.addLayout(Line_edit_layout1)

        # 第三行
        btn3 = self.button_x(self.ui_08, self.QPushButton_load)
        btn3.clicked.connect(self.ui_08_QPushButton_load_clicked)
        btn3_layout = QHBoxLayout()
        btn3_layout.addStretch(1)
        btn3_layout.addWidget(btn3)
        btn3_layout.addStretch(1)

        layout.addLayout(btn3_layout)
        layout.addStretch(1)
        return layout

    def ui_08_QPushButton_pbix_clicked(self):
        """选择 pbix
        
        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        le1 = self.findChild(QLineEdit, f"{self.ui_08}_{self.QLineEdit_pbix}")
        le1.setText(files)

        le1_str = le1.text()
        valid = Validator()
        le1_pbix = valid.is_pbix(le1_str)
        if not le1_pbix[0]:
            msg = {"value": f"{self.ui_08_dic[self.QLabel_pbix]['display']}:{le1_pbix[1]}"}
            self.msg_display("msg0901", msg)
            le1.setFocus()
            return

    def ui_08_QPushButton_dax_clicked(self):
        """选择 文件夹
        
        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        folder = str(file_diglog.getExistingDirectory())
        # files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        le2 = self.findChild(QLineEdit, f"{self.ui_08}_{self.QLineEdit_dax}")
        le2.setText(folder)
        le2_str = le2.text()
        valid = Validator()
        le2_isdir = valid.is_dir(le2_str)
        if not le2_isdir[0]:
            msg = {"value": f"{self.ui_08_dic[self.QLabel_dax]['display']}:{le2_isdir[1]}"}
            self.msg_display("msg0902", msg)
            le2.setFocus()
            return

    def ui_08_QPushButton_load_clicked(self):
        """执行

        :return:None
        :rtype: None
        """
        try:

            le1 = self.findChild(QLineEdit, f"{self.ui_08}_{self.QLineEdit_pbix}")
            le1_str = le1.text()
            valid = Validator()
            le1_pbix = valid.is_pbix(le1_str)
            if not le1_pbix[0]:
                msg = {"value": f"{self.ui_08_dic[self.QLabel_pbix]['display']}:{le1_pbix[1]}"}
                self.msg_display("msg0903", msg)
                le1.setFocus()
                return

            le2 = self.findChild(QLineEdit, f"{self.ui_08}_{self.QLineEdit_dax}")
            le2_str = le2.text()
            valid = Validator()
            le2_isdir = valid.is_dir(le2_str)
            if not le2_isdir[0]:
                msg = {"value": f"{self.ui_08_dic[self.QLabel_dax]['display']}:{le2_isdir[1]}"}
                self.msg_display("msg0904", msg)
                le2.setFocus()
                return

            # 初始化小于 10 的随机的进度
            self.progress_bar_display(int(random() * 10))
            self.thread0 = ThreadExtractPbix(le1_str)
            self.thread0.signal_dic.connect(self.extract_pbix_2_dax_callback)
            self.thread0.start()

            # 设置线程运行按钮不可点击
            if self.thread0.isRunning():
                self.btn_pbix_2_dax(False)
        except Exception:
            self.msg_display("msg0905")

    def extract_pbix_2_dax_callback(self, signal_dic):
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
                self.btn_pbix_2_dax(True)
                return
            if "error" in signal_dic and signal_dic["error"] == "PermissionError":
                self.msg_display("msg1303")  # 完成提示
                self.progress_bar_display()  # 进度条归零
                self.btn_pbix_2_dax(True)
                return
            if signal_dic["progress"] == 100:
                self.progress_bar_display(int(random() * 50 + 10))
                le_str = self.findChild(QLineEdit, f"{self.ui_08}_{self.QLineEdit_dax}").text()  # 文件夹
                le1_str = self.findChild(QLineEdit, f"{self.ui_08}_{self.QLineEdit_pbix}").text()  # pbix 路径
                pbit = Pbit()
                pbit.path_pbix_source = le1_str
                pbit.measure_export_pbix_2_folder(le_str)
                pbit.delete_folder_pbix_extract()
                self.progress_bar_display(100)
                msg = {"value": le_str}
                self.msg_display("msg0906", msg, is_info=True)
                self.progress_bar_display()
                self.btn_pbix_2_dax(True)
        except PermissionError:
            self.msg_display("msg1303")  # 完成提示

    def btn_pbix_2_dax(self, boolean):
        """按钮状态

        :param boolean: True False
        :type boolean: bool
        :return: None
        :rtype: None
        """
        self.btn_enabled_status(f"{self.ui_08}_{self.QPushButton_pbix}", boolean)
        self.btn_enabled_status(f"{self.ui_08}_{self.QPushButton_dax}", boolean)
        self.btn_enabled_status(f"{self.ui_08}_{self.QPushButton_load}", boolean)