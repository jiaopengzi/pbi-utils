# -*- encoding: utf-8 -*-
"""
@File           :   ui_06_pbix2pbit.py
@Time           :   2022-11-10, 周四, 18:13
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   生成 pbit
"""

from random import random

from PySide6.QtWidgets import (QComboBox, QFileDialog, QHBoxLayout,
                               QLineEdit, QVBoxLayout, QWidget)

from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod
from utils.methods import read_json
from utils.threads_jpz import ThreadCompile
from utils.validators import Validator


class Pbix2Pbit(UiMethod):
    """生成 pbit
    """
    ui_06 = "ui_06"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_pbix2pbit(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_06_dic = DISPLAY_CONFIG[self.ui_06]
        for key in self.ui_06_dic:
            setattr(self, key, key)

        # ui_06布局
        self.widget_06 = QWidget()
        self.layout = QVBoxLayout()
        # 添加子布局
        self.layout.addLayout(self.init_layout_ui_06_1_Lineedit())
        self.layout.addLayout(self.init_layout_ui_06_2_Lineedit())
        # self.layout.addLayout(self.init_layout_ui_06_3_Lineedit())
        self.layout.addLayout(self.init_layout_ui_06_4_combo())
        self.layout.addLayout(self.init_layout_ui_06_5_Lineedit())
        self.layout.addLayout(self.init_layout_ui_06_6_push_button_run())  # 第 7 行,按钮
        # self.layout.addLayout(self.init_layout_ui_06_7_progress_bar())  # 第 8 行,进度条
        self.layout.addStretch()  # 增加弹簧
        self.widget_06.setLayout(self.layout)
        self.progress_bar_display()
        return self.widget_06

    def init_layout_ui_06_1_Lineedit(self):
        """第 1 行

        :return: le_layout
        :rtype: QHBoxLayout
        """
        le_layout = self.Line_edit_layout_x(self.ui_06, self.QLabel_pbix, self.QLineEdit_pbix)
        btn = self.button_x(self.ui_06, self.QPushButton_pbix)
        btn.clicked.connect(self.ui_06_QPushButton_pbix_clicked)
        le_layout.addWidget(btn)
        return le_layout

    def ui_06_QPushButton_pbix_clicked(self):
        """第 1 行,选择按钮槽函数

        :return:None
        :rtype: None
        """
        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        le = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_pbix}")
        le.setText(files)

    def init_layout_ui_06_2_Lineedit(self):
        """第 2 行

        :return:le_layout
        :rtype: QHBoxLayout
        """
        le_layout = self.Line_edit_layout_x(self.ui_06, self.QLabel_config, self.QLineEdit_config)
        btn = self.button_x(self.ui_06, self.QPushButton_choose)
        btn.clicked.connect(self.ui_06_QPushButton_json_clicked)
        le_layout.addWidget(btn)
        return le_layout

    def ui_06_QPushButton_json_clicked(self):
        """第 2 行,选择按钮槽函数

        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.json")
        le = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_config}")
        le.setText(files)

        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(files)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_06_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0704", msg)
            le.setFocus()
            return
        try:
            # 加载 ui_06 设置选项卡
            self.combobox_update(f"{self.ui_06}_{self.QComboBox_measuretable}", files, "MeasureTable")
        except Exception:
            le.setText("")
            le.setFocus()
            self.msg_display("msg0701")

    # def init_layout_ui_06_3_Lineedit(self):
    #     """
    #     ui_06 第 3 行
    #     """
    #     le_layout = self.Line_edit_layout_x(self.ui_06, self.QLabel_pbit, self.QLineEdit_pbit)
    #     btn = self.button_x(self.ui_06, self.QPushButton_pbit)
    #     btn.clicked.connect(self.ui_06_QPushButton_pbit_clicked)
    #     le_layout.addWidget(btn)
    #     return le_layout

    # def ui_06_QPushButton_pbit_clicked(self):
    #     """
    #     ui_06 第 3 行 保存 pbit 槽函数
    #     """
    #     file_diglog = QFileDialog()
    #     files, _ = file_diglog.getSaveFileName(filter="*.pbit")
    #     le = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_pbit}")
    #     le.setText(files)

    def init_layout_ui_06_4_combo(self):
        """第 4 行

        :return: combo layout
        :rtype: QHBoxLayout
        """
        return self.combobox_layout_x(self.ui_06, self.QLabel_measuretable, self.QComboBox_measuretable, minimum_width=400)

    def init_layout_ui_06_5_Lineedit(self):
        """ 第 5 行

        :return: line edit layout
        :rtype: QHBoxLayout
        """
        return self.Line_edit_layout_x(self.ui_06, self.QLabel_measurefolder, self.QLineEdit_measurefolder)

    def init_layout_ui_06_6_push_button_run(self):
        """第 6 行

        :return:push_button_layout
        :rtype:QHBoxLayout
        """
        push_button_layout = QHBoxLayout()
        run_button = self.button_x(self.ui_06, self.QPushButton_create)
        run_button.clicked.connect(self.ui_06_QPushButton_create_clicked)  # 槽函数注意不要有括号
        push_button_layout.addStretch(1)
        push_button_layout.addWidget(run_button)
        push_button_layout.addStretch(1)
        return push_button_layout

    def ui_06_QPushButton_create_clicked(self):
        """第 6 行 执行按钮槽函数

        :return:None
        :rtype: None
        """

        try:

            # 获取控件信息
            le1 = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_pbix}")
            le2 = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_config}")
            # le3 = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_pbit}")
            cb4 = self.findChild(QComboBox, f"{self.ui_06}_{self.QComboBox_measuretable}")
            le5 = self.findChild(QLineEdit, f"{self.ui_06}_{self.QLineEdit_measurefolder}")
            # cb6 = self.findChild(QCheckBox, "ui_06_6_checkbox")

            # 获取控件文本

            le1_str = le1.text()
            le2_str = le2.text()

            # le3_str = le3.text()
            cb4_str = cb4.currentText()
            le5_str = le5.text()

            valid = Validator()

            le1_ispbix = valid.is_pbix(le1_str)
            le2_istext = valid.is_json(le2_str)
            # le3_is_pbit = valid.is_pbit(le3_str)
            cb4_istext = valid.is_text(cb4_str)
            le5_istext = valid.is_text(le5_str)

            # 点击前验证
            if not le1_ispbix[0]:
                msg = {"value": f"{self.ui_06_dic[self.QLabel_pbix]['display']}:{le1_ispbix[1]}"}
                self.msg_display("msg0702", msg)
                le1.setFocus()
                return
            if not le2_istext[0]:
                msg = {"value": f"{self.ui_06_dic[self.QLabel_config]['display']}:{le2_istext[1]}"}
                self.msg_display("msg0703", msg)
                le2.setFocus()
                return
            # if not le3_is_pbit[0]:
            #     msg = {"value": f"{self.ui_06_dic[self.QLineEdit_pbit]['display']}:{le3_is_pbit[1]}"}
            #     self.msg_display("msg0704", msg)
            #     le3.setFocus()
            #     return
            if not cb4_istext[0]:
                msg = {"value": f"{self.ui_06_dic[self.QLabel_measuretable]['display']}:{cb4_istext[1]}"}
                self.msg_display("msg0705", msg)
                cb4.setFocus()
                return
            if not le5_istext[0]:
                msg = {"value": f"{self.ui_06_dic[self.QLabel_measurefolder]['display']}:{le5_istext[1]}"}
                self.msg_display("msg0706", msg)
                le5.setFocus()
                return

            # 读取json获取内容页
            dic = read_json(le2_str)
            new_report_pages_list = dic["PageGroup"]
            # 初始化小于 10 的随机的进度
            self.progress_bar_display(int(random() * 10))
            # cb6_state = bool(cb6.isChecked())
            # self.thread = ThreadCompile(le1_str, le2_str, le3_str, new_report_pages_list, cb4_str, le5_str)
            self.thread = ThreadCompile(le1_str, le2_str, new_report_pages_list, cb4_str, le5_str)
            self.thread.signal_dic.connect(self.compile_progress_callback)
            self.thread.start()
            # 设置线程运行按钮不可点击
            if self.thread.isRunning():
                self.btn_enabled_status(f"{self.ui_06}_{self.QPushButton_create}", False)
        except Exception:
            self.msg_display("msg0707")

    def compile_progress_callback(self, signal_dic):
        """进度条的回调函数

        :param signal_dic:  {"progress": 0, "status": True}
        :type signal_dic: dict
        :return: None
        :rtype: None
        """

        self.progress_bar_display(int(signal_dic["progress"]))

        if "error" in signal_dic and signal_dic["error"] == "extract_error":
            self.msg_display("msg1301")  # 完成提示
            self.compile_progress_callback_message()
        elif "error" in signal_dic and signal_dic["error"] == "compile_error":
            self.msg_display("msg1302")  # 完成提示
            self.compile_progress_callback_message()
        elif "error" in signal_dic and signal_dic["error"] == "PermissionError":
            self.msg_display("msg1303")  # 完成提示
            self.compile_progress_callback_message()
        elif "error" in signal_dic and signal_dic["error"] == "not_match":
            self.msg_display("msg0708")  # 完成提示
            self.compile_progress_callback_message()
        elif not signal_dic["status"]:
            self.msg_display("msg0707")  # 错误完成提示
            self.compile_progress_callback_message()
        elif signal_dic["progress"] == 100:
            # 完成提示
            self.msg_display("msg0709", is_info=True)
            self.compile_progress_callback_message()

    def compile_progress_callback_message(self):
        """消息闪现

        :return:None
        :rtype:None
        """
        # 进度条归零
        self.progress_bar_display()
        # 恢复按钮
        self.btn_enabled_status(f"{self.ui_06}_{self.QPushButton_create}", True)