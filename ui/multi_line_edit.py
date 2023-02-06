# -*- encoding: utf-8 -*-
"""
@File           :   multi_line_edit.py
@Time           :   2022-11-10, 周四, 16:54
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   多行编辑
"""

import sys

from PySide6.QtCore import QSize
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (QAbstractItemView, QApplication,
                               QComboBox, QDialog, QHBoxLayout, QLabel,
                               QLineEdit, QListWidget, QVBoxLayout)

from config import qss
from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod
from utils.validators import Validator


class MultiLineEdit(QDialog, UiMethod):
    """多行编辑框
    """
    multi_line = "multi_line"

    def __init__(self, list_left_=None, list_right_=None, combo_has=None, combo_items=None, *args, **kwargs):
        """初始化参数

        :param list_left_: 左边的list
        :type list_left_: list
        :param list_right_: 右边的list
        :type list_right_: list
        :param combo_has: 是否有下拉框, 默认 None
        :type combo_has:bool
        :param combo_items:下拉框的数据源列表
        :type combo_items:list
        """
        super().__init__(*args, **kwargs)

        self.multi_line_dic = DISPLAY_CONFIG[self.multi_line]
        for key in self.multi_line_dic:
            setattr(self, key, key)

        self.list_left = list_left_
        self.list_right = list_right_
        self.combo_has = combo_has
        self.combo_items = combo_items
        self.combo_item = None
        self.ui_multi_line_edit()
        self.list_load_left()
        self.list_load_right()

    def ui_multi_line_edit(self):
        """初始化UI
        """
        self.setWindowTitle(self.multi_line_dic[self.Title_display]['display'])
        self.setMinimumWidth(800)
        self.setMinimumHeight(495)
        self.layout_all()
        self.setStyleSheet(qss())
        icon = QIcon()
        icon.addFile(':/icon/image/logo.svg', QSize(), QIcon.Normal, QIcon.Off)
        self.setWindowIcon(icon)

    def layout_all(self):
        """添加左中右 3 个布局
        """
        self.layout = QHBoxLayout()
        self.layout.addLayout(self.layout_left_init())
        self.layout.addLayout(self.layout_middle_init())
        self.layout.addLayout(self.layout_right_init())
        self.setLayout(self.layout)

    def layout_left_init(self):
        """左边布局
        """
        self.layout_left = QVBoxLayout()
        self.line_edit = QLineEdit()
        self.line_edit.setPlaceholderText(self.multi_line_dic[self.QLineEdit_search]['placeholder'])
        self.line_edit.textChanged.connect(self.line_edit_text_changed)
        self.list_widget_left = QListWidget()
        self.list_widget_left.setSelectionMode(QAbstractItemView.SelectionMode.MultiSelection)  # 设置可以多选

        self.layout_left.addWidget(self.line_edit)
        self.layout_left.addWidget(self.list_widget_left)

        return self.layout_left

    def layout_middle_init(self):
        """中间布局
        """
        self.layout_middle = QVBoxLayout()
        self.btn_add = self.button_x(self.multi_line, self.QPushButton_add)
        self.btn_del = self.button_x(self.multi_line, self.QPushButton_del)
        self.btn_all = self.button_x(self.multi_line, self.QPushButton_left_select_all)
        self.btn_can = self.button_x(self.multi_line, self.QPushButton_cancel_select_all)
        self.btn_cle = self.button_x(self.multi_line, self.QPushButton_right_clear_all)

        self.btn_add.clicked.connect(self.btn_add_clicked)
        self.btn_del.clicked.connect(self.btn_del_clicked)
        self.btn_all.clicked.connect(self.btn_all_clicked)
        self.btn_can.clicked.connect(self.btn_can_clicked)
        self.btn_cle.clicked.connect(self.btn_cle_clicked)

        self.layout_middle.addStretch()
        self.layout_middle.addWidget(self.btn_add)
        self.layout_middle.addWidget(self.btn_del)
        self.layout_middle.addWidget(self.btn_all)
        self.layout_middle.addWidget(self.btn_can)
        self.layout_middle.addWidget(self.btn_cle)
        self.layout_middle.addStretch()

        return self.layout_middle

    def layout_right_init(self):
        """右边布局
        """
        self.layout_right = QVBoxLayout()
        self.layout_right_top = QHBoxLayout()
        if self.combo_has:
            self.combo_display()
        self.layout_right_top.addStretch()
        self.btn_commit = self.button_x(self.multi_line, self.QPushButton_commit)
        self.btn_commit.clicked.connect(self.btn_commit_clicked)
        self.layout_right_top.addWidget(self.btn_commit)

        self.list_widget_right = QListWidget()
        self.list_widget_right.setSelectionMode(QAbstractItemView.SelectionMode.MultiSelection)  # 设置可以多选

        self.layout_right.addLayout(self.layout_right_top)
        self.layout_right.addWidget(self.list_widget_right)

        return self.layout_right

    def combo_display(self):
        """显示下拉框

        :return:None
        :rtype:None
        """
        self.label = QLabel(self.multi_line_dic[self.QLabel_measure_table]['display'])
        self.combo = QComboBox()
        self.combo.setMinimumWidth(200)
        for item in self.combo_items:
            self.combo.addItem(item)
        self.layout_right_top.addWidget(self.label)
        self.layout_right_top.addWidget(self.combo)

    @staticmethod
    def search_fuzzy(list_search, keyword):
        """list 模糊查询

        :param list_search:需要被查找的数据源
        :type list_search:list
        :param keyword:匹配的关键字
        :type keyword:str
        :return: 返回搜索到的结果 list
        :rtype:list
        """

        if keyword:
            return [item for item in list_search if keyword in item]
        return list_search

    def line_edit_text_changed(self):
        """查询框模糊查询结果,及时显示在左侧

        :return: None
        :rtype: None
        """

        if self.list_left is not None:
            keyword = self.line_edit.text()
            list_temp = self.search_fuzzy(self.list_left, keyword)
            self.list_widget_left.clear()
            self.list_widget_left.addItems(list_temp)

    def list_load_left(self):
        """加载左侧 list 数据

        :return: None
        :rtype: None
        """
        if self.list_left is not None:
            self.list_widget_left.addItems(self.list_left)

    def list_load_right(self):
        """加载右侧 list 数据

        :return: None
        :rtype: None
        """
        if self.list_right is not None:
            self.list_widget_right.addItems(self.list_right)

    def btn_add_clicked(self):
        """左侧添加到右侧

        :return: None
        :rtype: None
        """
        selected_items = self.list_widget_left.selectedItems()
        if len(selected_items) <= 0:
            self.msg_display("msg0101")
            return
        text_list = [i.text() for i in selected_items]
        right_text_list = self.get_list_widget_text(self.list_widget_right)
        for item in text_list:
            if item in right_text_list:
                continue
            self.list_widget_right.addItem(item)
        self.list_widget_selected_cancel(self.list_widget_left)

    def btn_del_clicked(self):
        """删除右侧选中的文本

        :return: None
        :rtype: None
        """
        selected_items = self.list_widget_right.selectedItems()
        if len(selected_items) <= 0:
            return
        for item in selected_items:
            self.list_widget_right.takeItem(self.list_widget_right.row(item))

    def btn_all_clicked(self):
        """左侧全选

        :return: None
        :rtype: None
        """
        count = self.list_widget_left.count()
        for i in range(count):
            item = self.list_widget_left.item(i)
            item.setSelected(True)

    def btn_can_clicked(self):
        """取消左右两侧的 list 全选

        :return: None
        :rtype: None
        """
        self.list_widget_selected_cancel(self.list_widget_left)
        self.list_widget_selected_cancel(self.list_widget_right)

    def btn_cle_clicked(self):
        """
        清空右边

        :return: None
        :rtype: None
        """
        self.list_widget_right.clear()

    @staticmethod
    def get_list_widget_text(list_widget):
        """获取 list 中的文本

        :return: list_text
        :rtype: list
        """
        count = list_widget.count()
        list_text = []
        for i in range(count):
            item = list_widget.item(i)
            list_text.append(item.text())
        return list_text

    @staticmethod
    def list_widget_selected_cancel(list_widget):
        """取消 list 选中, 参数为 list_widget

        :return: None
        :rtype: None
        """
        items = list_widget.selectedItems()
        if not items:
            return
        for item in items:
            item.setSelected(False)

    def btn_commit_clicked(self):
        """提交

        :return: None
        :rtype: None
        """
        if self.combo_has:
            combo1_str = self.combo.currentText()
            valid = Validator()
            combo1_istext = valid.is_text(combo1_str)
            if not combo1_istext[0]:
                dic = {"value": f"度量值表:{combo1_istext[1]}"}
                self.msg_display("msg0102", dic)
                self.combo.setFocus()
                return
            self.combo_item = self.combo.currentText()
        if text_list := self.get_list_widget_text(self.list_widget_right):
            self.list_right = text_list
            return
        self.msg_display("msg0103")

        # return text_list

# if __name__ == '__main__':
#     list_left = ["jiaopengzi", "hahah", "nihao", "焦棚子", "123", "焦棚子123", "pbi@", "@pbi"]
#     list_right = ["jiaopengzi", "hahah"]
#     app = QApplication(sys.argv)
#     demo_widget = MultiLineEdit(combo_has=1, combo_items=list_left)
#     # demo_widget = MultiLineEdit(list_left, list_right)
#     demo_widget.show()
#     sys.exit(app.exec())