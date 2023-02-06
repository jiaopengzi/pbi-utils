# -*- encoding: utf-8 -*-
# -*- encoding: utf-8 -*-
"""
@File           :   dialog_radio.py
@Time           :   2022-11-10, 周四, 15:1
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   单选对话框
"""

import sys

from PySide6.QtCore import QSize
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication, QDialog, QHBoxLayout, QRadioButton, QVBoxLayout

from config import qss
from configfiles import DISPLAY_CONFIG


class Radio(QDialog):
    """
    display_dict:显示的字典
    radio_key:原有的选项
    """
    radio_result = None
    dialog_radio = "dialog_radio"

    def __init__(self, display_dict: dict = None, radio_key: int = None, window_title: str = None, *args, **kwargs):
        """初始化

        :param display_dict:显示的字典
        :type display_dict:dict
        :param radio_key:默认选项
        :type radio_key:int
        :param window_title:窗口标题
        :type window_title:str
        """
        super().__init__(*args, **kwargs)

        self.dialog_radio_dic = DISPLAY_CONFIG[self.dialog_radio]
        for key in self.dialog_radio_dic:
            setattr(self, key, key)

        self.display_dict = display_dict
        self.radio_key = radio_key
        self.window_title = window_title
        self.ui_radio_layout()

    def ui_radio_layout(self):
        """初始化UI
        :return: None
        :rtype: None
        """
        if self.window_title is None:
            self.setWindowTitle(self.dialog_radio_dic[self.Title_display]['display'])
        else:
            self.setWindowTitle(self.window_title)
        self.setMinimumWidth(200)
        # self.setMinimumHeight(300)
        self.layout_all()
        # qss 样式
        self.setStyleSheet(qss())
        icon = QIcon()
        icon.addFile(':/icon/image/logo.svg', QSize(), QIcon.Normal, QIcon.Off)
        self.setWindowIcon(icon)

    def layout_all(self):
        """
        添加 1 个布局
        """
        self.layout = QHBoxLayout()
        self.layout.addLayout(self.layout_radio_layout())
        self.setLayout(self.layout)

    def layout_radio_layout(self):
        """
        中间布局
        """
        self.layout_radio = QVBoxLayout()

        dict = self.display_dict
        # 选项
        for key in dict:
            radio_ = QRadioButton(dict[key], self)
            radio_.setObjectName(f"radio_{key}")
            radio_.toggled.connect(self.radio_choice)
            if key == self.radio_key:
                radio_.setChecked(True)
            self.layout_radio.addWidget(radio_)

        return self.layout_radio

    def radio_choice(self):
        """选择按钮发射信号
        :return:None
        :rtype:None
        """
        radio_ = self.sender()
        if radio_.isChecked():
            key = radio_.objectName()
            self.radio_result = key.split("radio_")[1]


# if __name__ == '__main__':
#     DISPLAYOPTON = {1: "1=>调整到页面大小",
#                     2: "2=>适应宽度",
#                     3: "3=>实际大小"}
#
#     VERTICAL_ALIGNMENT = {"'Middle'": "'Middle'=>垂直对齐：中",
#                           "'Top'"   : "'Top'=>垂直对齐：上"}
#
#     VISIBILITY = {0: "0=>显示",
#                   1: "1=>隐藏"}
#     list_key = (1, 2, 3)
#
#     list_dic2 = ("'Middle'", "'Top'")
#     radio = "'Top'"
#     app = QApplication(sys.argv)
#
#     demo_widget = Radio(VERTICAL_ALIGNMENT, radio)
#     demo_widget.show()
#     sys.exit(app.exec())