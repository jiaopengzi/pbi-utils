# -*- encoding: utf-8 -*-
"""
@File           :   ui_01_config_json_init.py
@Time           :   2022-11-10, 周四, 17:11
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   配置文件初始化
"""

from random import random

from PySide6.QtWidgets import QCheckBox, QFileDialog, QHBoxLayout, QLineEdit, QVBoxLayout, QWidget

from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod
from utils.threads_jpz import ThreadCreateJson
from utils.validators import Validator


class JsonInit(UiMethod):
    """初始化 config.json
    """
    ui_01 = "ui_01"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_json_init(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_01_dic = DISPLAY_CONFIG[self.ui_01]
        for key in self.ui_01_dic:
            setattr(self, key, key)

        # view1布局
        self.widget = QWidget()
        self.layout = QVBoxLayout()
        # 添加子布局
        self.layout.addLayout(self.ui_01_row_01())
        self.layout.addLayout(self.ui_01_row_02())
        self.layout.addLayout(self.ui_01_row_03())
        self.layout.addLayout(self.ui_01_row_04())  # 第 4 行 选择框
        self.layout.addLayout(self.ui_01_row_05())
        self.layout.addStretch()  # 增加弹簧
        self.widget.setLayout(self.layout)
        self.progress_bar_display()
        return self.widget

    def ui_01_row_01(self):
        """第 1 行

        :return: le_layout
        :rtype: QHBoxLayout
        """

        le_layout = self.Line_edit_layout_x(self.ui_01, self.QLabel_pbix, self.QLineEdit_pbix)
        btn = self.button_x(self.ui_01, self.QPushButton_choose)
        btn.clicked.connect(self.ui_01_QPushButton_choose_clicked)
        le_layout.addWidget(btn)
        return le_layout

    def ui_01_QPushButton_choose_clicked(self):
        """第 1 行,选择按钮槽函数

        :return: None
        :rtype: None
        """
        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        le = self.findChild(QLineEdit, f"{self.ui_01}_{self.QLineEdit_pbix}")
        le.setText(files)

    def ui_01_row_02(self):
        """第 2 行

        :return: le_layout
        :rtype: QHBoxLayout
        """
        return self.Line_edit_layout_x(self.ui_01,
                                       self.QLabel_content,
                                       self.QLineEdit_content,
                                       reg_str="([1-9][0-9]?,)*([1-9][0-9]?)$")  # 正则只允许两位

    def ui_01_row_03(self):
        """第 3 行

        :return: le_layout
        :rtype: QHBoxLayout
        """
        le_layout = self.Line_edit_layout_x(self.ui_01, self.QLabel_config, self.QLineEdit_config)
        btn = self.button_x(self.ui_01, self.QPushButton_save)
        btn.clicked.connect(self.ui_01_QPushButton_save_clicked)
        le_layout.addWidget(btn)
        return le_layout

    def ui_01_QPushButton_save_clicked(self):
        """第 3 行,保存按钮槽函数

        :return:None
        :rtype:None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getSaveFileName(filter="*.json")
        le = self.findChild(QLineEdit, f"{self.ui_01}_{self.QLineEdit_config}")
        le.setText(files)

    def ui_01_row_04(self):
        """第4行

        :return: 第四行的布局
        :rtype: QHBoxLayout
        """

        checkbox_layout = QHBoxLayout()
        checkbox = QCheckBox(self.ui_01_dic[self.QCheckBox_isencrypt]["display"])
        checkbox.setObjectName(f"{self.ui_01}_{self.QCheckBox_isencrypt}")
        checkbox_layout.addStretch(1)
        checkbox_layout.addWidget(checkbox)
        checkbox_layout.addStretch(1)
        return checkbox_layout

    def ui_01_row_05(self):
        """第 5 行

        :return: layout
        :rtype: QHBoxLayout
        """
        layout = QHBoxLayout()
        btn = self.button_x(self.ui_01, self.QPushButton_init)
        btn.clicked.connect(self.ui_01_QPushButton_init_clicked)
        layout.addStretch(1)
        layout.addWidget(btn)
        layout.addStretch(1)
        return layout

    def ui_01_QPushButton_init_clicked(self):
        """第 5 行,槽函数

        :return: None
        :rtype: None
        """

        try:
            # 获取控件信息
            # 1、初始化 ReportPages.json
            le1 = self.findChild(QLineEdit, f"{self.ui_01}_{self.QLineEdit_pbix}")
            le2 = self.findChild(QLineEdit, f"{self.ui_01}_{self.QLineEdit_content}")
            le3 = self.findChild(QLineEdit, f"{self.ui_01}_{self.QLineEdit_config}")
            cb4 = self.findChild(QCheckBox, f"{self.ui_01}_{self.QCheckBox_isencrypt}")

            # 获取控件文本
            le1_str = le1.text()
            le2_str = le2.text()
            le3_str = le3.text()
            cb4_state = bool(cb4.isChecked())

            valid = Validator()

            ispbix = valid.is_pbix(le1_str)
            istext = valid.is_text(le2_str)
            is_json = valid.is_json(le3_str, is_create=True)

            # 点击前验证
            if not ispbix[0]:
                msg = {"value": f"{self.ui_01_dic[self.QLabel_pbix]['display']}:{ispbix[1]}"}
                self.msg_display("msg0201", msg)
                le1.setFocus()
                return
            if not istext[0]:
                msg = {"value": f"{self.ui_01_dic[self.QLabel_content]['display']}:{istext[1]}"}
                self.msg_display("msg0202", msg)
                le2.setFocus()
                return
            if not is_json[0]:
                msg = {"value": f"{self.ui_01_dic[self.QLabel_config]['display']}:{is_json[1]}"}
                self.msg_display("msg0203", msg)
                le3.setFocus()
                return
            # 初始化小于 10 的随机的进度
            self.progress_bar_display(int(random() * 10))
            self.thread = ThreadCreateJson(self, le1_str, le2_str, le3_str, cb4_state)
            self.thread.signal_dic.connect(self.create_json_signal_dic_callback)
            self.thread.start()

            # 设置线程运行按钮不可点击
            if self.thread.isRunning():
                self.btn_enabled_status(f"{self.ui_01}_{self.QPushButton_init}", False)

        except Exception:
            self.msg_display("msg0204")

    def create_json_signal_dic_callback(self, signal_dic):
        """进度条的回调函数

        :param signal_dic: self.signal_dic.emit({"progress": int(random() * 80), "status": True})
        :type signal_dic: dict
        :return: None
        :rtype: None
        """

        if signal_dic["status"]:
            self.progress_bar_display(signal_dic["progress"])  # 进度条

            if signal_dic["progress"] == 100:
                self.msg_display("msg0205", is_info=True)  # 完成提示
                self.btn_enabled_status(f"{self.ui_01}_{self.QPushButton_init}", True)  # 恢复按钮
                self.progress_bar_display()  # 进度条归零

        elif "error" in signal_dic and signal_dic["error"] == "extract_error":
            self.msg_btn_progress("msg1301")
        elif "error" in signal_dic and signal_dic["error"] == "PermissionError":
            self.msg_btn_progress("msg1303")
        else:
            self.msg_btn_progress("msg0206")

    def msg_btn_progress(self, msg) -> None:
        """消息闪现 按钮恢复 进度条归零

        :param msg:消息编号
        :return:None
        """

        self.msg_display(msg)
        self.btn_enabled_status(f"{self.ui_01}_{self.QPushButton_init}", True)  # 恢复按钮
        self.progress_bar_display()  # 进度条归零