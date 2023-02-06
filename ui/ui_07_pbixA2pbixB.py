# -*- encoding: utf-8 -*-
"""
@File           :   ui_07_pbixA2pbixB.py
@Time           :   2022-11-10, 周四, 19:13
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   pbixA 2 pbixB
"""

from random import random

from PySide6.QtWidgets import QFileDialog, QHBoxLayout, QLineEdit, QVBoxLayout, QWidget

from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod
from ui.multi_line_edit import MultiLineEdit
from utils.pbit import Pbit
from utils.threads_jpz import ThreadExtractPbix
from utils.validators import Validator


class PbixA2PbixB(UiMethod):
    """pbixA 2 pbixB
    """

    ui_07 = "ui_07"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_pbixa_2_pbixb(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_07_dic = DISPLAY_CONFIG[self.ui_07]
        for key in self.ui_07_dic:
            setattr(self, key, key)

        self.widget_07 = QWidget()
        self.ui_07_layout = QVBoxLayout()
        # 添加子布局
        self.ui_07_layout.addLayout(self.ui_pbixa_2_pbixb_layout_v1())
        self.widget_07.setLayout(self.ui_07_layout)
        self.progress_bar_display()
        return self.widget_07

    def ui_pbixa_2_pbixb_layout_v1(self):
        """布局 v1

        :return: layout
        :rtype: QVBoxLayout
        """

        layout = QVBoxLayout()
        # 第一行
        Line_edit_layout0 = self.Line_edit_layout_x(self.ui_07, self.QLabel_pbixA, self.QLineEdit_pbixA)
        btn1 = self.button_x(self.ui_07, self.QPushButton_pbixA)
        btn1.clicked.connect(self.ui_07_QPushButton_pbixA_clicked)
        Line_edit_layout0.addWidget(btn1)
        layout.addLayout(Line_edit_layout0)

        # 第二行
        Line_edit_layout1 = self.Line_edit_layout_x(self.ui_07, self.QLabel_pbixB, self.QLineEdit_pbixB)
        btn2 = self.button_x(self.ui_07, self.QPushButton_pbixB)
        btn2.clicked.connect(self.ui_07_QPushButton_pbixB_clicked)
        Line_edit_layout1.addWidget(btn2)
        layout.addLayout(Line_edit_layout1)

        # 第三行
        btn3 = self.button_x(self.ui_07, self.QPushButton_load)
        btn3.clicked.connect(self.ui_07_QPushButton_load_clicked)
        btn3_layout = QHBoxLayout()
        btn3_layout.addStretch(1)
        btn3_layout.addWidget(btn3)
        btn3_layout.addStretch(1)

        layout.addLayout(btn3_layout)
        layout.addStretch(1)
        return layout

    def ui_07_QPushButton_pbixA_clicked(self):
        """按钮选择 pbixA

        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        leB = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixA}")
        leB.setText(files)

        leB_str = leB.text()
        valid = Validator()
        leB_pbix = valid.is_pbix(leB_str)
        if not leB_pbix[0]:
            msg = {"value": f"{self.ui_07_dic[self.QLabel_pbixA]['display']}:{leB_pbix[1]}"}
            self.msg_display("msg0801", msg)
            leB.setFocus()
            return

    def ui_07_QPushButton_pbixB_clicked(self):
        """按钮选择 pbixB

        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.pbix")
        le2 = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixB}")
        le2.setText(files)
        le2_str = le2.text()
        valid = Validator()
        le2_ispbix = valid.is_pbix(le2_str)
        if not le2_ispbix[0]:
            msg = {"value": f"{self.ui_07_dic[self.QLabel_pbixB]['display']}:{le2_ispbix[1]}"}
            self.msg_display("msg0802", msg)
            le2.setFocus()
            return

    def ui_07_QPushButton_load_clicked(self):
        """加载数据

        :return:None
        :rtype: None
        """
        try:

            valid = Validator()

            leA = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixA}")
            le_strA = leA.text()
            le_pbixA = valid.is_pbix(le_strA)
            if not le_pbixA[0]:
                msg = {"value": f"{self.ui_07_dic[self.QLabel_pbixA]['display']}:{le_pbixA[1]}"}
                self.msg_display("msg0803", msg)
                leA.setFocus()
                return

            leB = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixB}")
            le_strB = leB.text()
            le_pbixB = valid.is_pbix(le_strB)
            if not le_pbixB[0]:
                msg = {"value": f"{self.ui_07_dic[self.QLabel_pbixB]['display']}:{le_pbixB[1]}"}
                self.msg_display("msg0804", msg)
                leB.setFocus()
                return

            if le_strA == le_strB:
                self.msg_display("msg0809")
                return

            # 初始化小于 10 的随机的进度
            self.progress_bar_display(int(random() * 10))

            self.id_list = [0, 1]

            self.thread0 = ThreadExtractPbix(le_strA, self.id_list[0])
            self.thread1 = ThreadExtractPbix(le_strB, self.id_list[1])

            self.thread0.signal_dic.connect(self.extract_pbix_callback)
            self.thread1.signal_dic.connect(self.extract_pbix_callback)

            self.thread0.start()
            self.thread1.start()

            # 设置线程运行按钮不可点击
            if self.thread0.isRunning():
                self.progress_all = {}
                self.btn_pbixa_2_pbixb(False)
        except Exception:
            self.msg_display("msg0805")

    def extract_pbix_callback(self, signal_dic):
        """提后的回调函数

        :param signal_dic: 字典中五元素，依次是 线程序号，进度数值，提取的文件夹,状态，错误及错误内容
        :type signal_dic: dict
        :return:None
        :rtype:None
        """

        if "error" in signal_dic and signal_dic["error"] == "extract_error":
            # , "error": "extract_error"
            self.msg_display("msg1301")  # 错误提示
            self.progress_bar_display()  # 进度条归零
            self.btn_pbixa_2_pbixb(True)
            return
        if signal_dic["progress"]:
            if signal_dic["id"] == self.id_list[0]:
                self.path_left = signal_dic["folder"]
            else:
                self.path_right = signal_dic["folder"]

            self.progress_all[f"progress_{signal_dic['id']}"] = signal_dic["progress"]

            progress = sum(self.progress_all[value] for value in self.progress_all) / 2
            self.progress_bar_display(progress)

            if progress == 100:
                self.load_multi_line_edit()
                self.btn_pbixa_2_pbixb(True)
        else:
            self.msg_display("msg0806")

    def load_multi_line_edit(self):
        """加载多行编辑框

        :return: None
        :rtype: None
        """
        try:
            leA_str = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixA}").text()
            leB_str = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixB}").text()
            self.progress_bar_display(0)

            pbitA = Pbit()
            pbitA.path_pbix_source = leA_str
            pbitB = Pbit()
            pbitB.path_pbix_source = leB_str
            # "table->name": {"name": name, "folder": path_measure_table, "table": table, "dax": dax, "json": json}
            measures_left = pbitA.get_measures(folder_pbix_extract_x=self.id_list[0])
            # measures_right = pbit.get_measures(".dax", self.id_list[1])

            measure_table_right = pbitB.get_all_tables(self.id_list[1])

            list_left = list(measures_left)
            list_right = []

            multi = MultiLineEdit(list_left, list_right, True, measure_table_right)
            multi.exec()
            table_right = multi.combo_item
            list_right = multi.list_right

            if list_right and table_right:
                self.progress_bar_display(int(random() * 10))
                # "table->name": {"name": name, "folder": path_measure_table, "table": table, "dax": dax, "json": json}
                name_src = []
                dax_src = []
                json_src = []

                for measure in list_right:
                    name_src.append(measures_left[measure]["name"])
                    dax_src.append(measures_left[measure]["dax"])
                    json_src.append(measures_left[measure]["json"])

                pbitB.folder_temp = pbitB.folder_pbix_extract(self.id_list[1])
                # 删除重复度量值
                pbitB.remove_repeat_measure(name_src, folder_pbix_extract_x=self.id_list[1])
                # A 2 B 导入
                pbitB.measure_import_pbixa_2_pbixb(table_right, dax_src, self.id_list[1])
                pbitB.measure_import_pbixa_2_pbixb(table_right, json_src, self.id_list[1])

                self.progress_bar_display(int(random() * 60 + 10))
                le_text = self.findChild(QLineEdit, f"{self.ui_07}_{self.QLineEdit_pbixB}").text()

                pbitB.path_pbit_target = f"{le_text[:-1]}t"
                pbitB.pbi_tools_command_compile()
                pbitA.delete_folder_pbix_extract(self.id_list[0])
                if not pbitB.pbi_tools_command_compile_status:
                    self.msg_display("msg1302")
                    self.progress_bar_display()
                    return

                self.progress_bar_display(100)
                msg = {"value": pbitB.path_pbit_target}
                self.msg_display("msg0807", msg, is_info=True)
                self.progress_bar_display()
        except PermissionError:
            self.msg_display("msg1303")  # 完成提示
        except Exception:
            self.msg_display("msg0808")

    def btn_pbixa_2_pbixb(self, boolean):
        """按钮状态

        :param boolean: True False
        :type boolean: bool
        :return: None
        :rtype: None
        """
        self.btn_enabled_status(f"{self.ui_07}_{self.QPushButton_pbixA}", boolean)
        self.btn_enabled_status(f"{self.ui_07}_{self.QPushButton_pbixB}", boolean)
        self.btn_enabled_status(f"{self.ui_07}_{self.QPushButton_load}", boolean)