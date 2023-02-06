# -*- encoding: utf-8 -*-
"""
@File           :   ui_00_home.py
@Time           :   2022-11-10, 周四, 17:2
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   首页
"""

from PySide6.QtWidgets import QHBoxLayout, QLabel, QVBoxLayout, QWidget

from configfiles import DISPLAY_CONFIG


class Home(QWidget):
    """首页
    """

    home = "home"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_home(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.home_dic = DISPLAY_CONFIG[self.home]
        for key in self.home_dic:
            setattr(self, key, key)

        self.widget_home = QWidget()
        vbox = QVBoxLayout()
        hbox = QHBoxLayout()
        label_home = QLabel(self.home_dic[self.QLabel_home]['display'])
        vbox.addWidget(label_home)
        hbox.addStretch()
        hbox.addLayout(vbox)
        hbox.addStretch()
        self.widget_home.setLayout(hbox)
        self.progress_bar_display()
        return self.widget_home