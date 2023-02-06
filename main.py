# -*- encoding: utf-8 -*-
"""
@File           :   main.py
@Time           :   2022-11-10, 周四, 21:49
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   入口文件
"""

import sys

from PySide6.QtWidgets import QApplication

from ui.ui_main import UiMainWindow

app = QApplication(sys.argv)
main = UiMainWindow()
# main.radio_pbix_open_choose()
sys.exit(app.exec())