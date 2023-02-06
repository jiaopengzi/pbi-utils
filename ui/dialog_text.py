# -*- encoding: utf-8 -*-
"""
@File           :   dialog_text.py
@Time           :   2022-11-10, 周四, 15:1
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   多行文字编辑
"""

import sys

from PySide6.QtCore import QSize, Qt
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (QApplication, QDialog, QHBoxLayout, QPlainTextEdit,
                               QTableWidgetItem, QVBoxLayout)

from config import qss
from configfiles import DISPLAY_CONFIG
from ui.method import UiMethod


class Text(QDialog, UiMethod):
    """多行文字编辑
    """
    text_list_result = None
    dialog_text = "dialog_text"

    def __init__(self, textList=None, path=None, table=None, name_table=None, data_category=None, measure_table=None, *args, **kwargs):
        """初始化

        :param textList:文本列表
        :type textList:list
        :param path:路径
        :type path:str
        :param table:表格对象
        :type table:QTableWidget
        :param name_table:表格名称
        :type name_table:str
        :param data_category:数据类别
        :type data_category:dict
        :param measure_table:度量值表格
        :type measure_table:str
        """
        super().__init__(*args, **kwargs)

        self.dialog_text_dic = DISPLAY_CONFIG[self.dialog_text]
        for key in self.dialog_text_dic:
            setattr(self, key, key)
        self.text_list = textList
        self.path = path
        self.table = table
        self.name_table = name_table
        self.data_category = data_category
        self.measure_table = measure_table
        self.ui_text()

    def ui_text(self):
        """
        初始化UI
        """
        self.setWindowTitle(self.dialog_text_dic[self.Title_display]['display'])
        self.setMinimumWidth(500)
        self.setMinimumHeight(500)
        self.layout_all()
        # qss 样式
        self.setStyleSheet(qss())
        icon = QIcon()
        icon.addFile(':/icon/image/logo.svg', QSize(), QIcon.Normal, QIcon.Off)
        self.setWindowIcon(icon)
        # self.set_plain_text(self.plain_text)

    def layout_all(self):
        """
        添加 1 个布局
        """
        self.layout = QHBoxLayout()
        self.layout.addLayout(self.layout_text())
        self.setLayout(self.layout)

    def layout_text(self):
        """text 布局

        :return:None
        :rtype:None
        """

        layout = QVBoxLayout()
        btn = self.button_x(self.dialog_text, self.QPushButton_commit)
        btn.clicked.connect(self.save_text_multi_line_to_table)
        text_edit = QPlainTextEdit()
        text_edit.setObjectName(f"{self.dialog_text_dic}_{self.QPlainTextEdit_content2}")
        text_edit.setPlaceholderText(self.dialog_text_dic[self.QPlainTextEdit_content2]['placeholder'])
        if self.table:
            text_edit.setPlaceholderText(self.dialog_text_dic[self.QPlainTextEdit_content1]['placeholder'])
        plain_text = "\n".join(self.text_list)
        text_edit.setPlainText(plain_text)
        layout.addWidget(btn)
        layout.addWidget(text_edit)
        return layout

    def save_text_multi_line_to_table(self):
        """保存文本到表格

        :return: None
        :rtype: None
        """

        text_edit = self.findChild(QPlainTextEdit, f"{self.dialog_text_dic}_{self.QPlainTextEdit_content2}")
        text = text_edit.toPlainText().split("\n")
        text_list_result = list(set(text))  # 去重
        text_list_result.sort(key=text.index)
        blank_count = text_list_result.count("")
        for _ in range(blank_count):
            text_list_result.remove("")  # 移除空
        self.text_list_result = text_list_result

        if self.table:  # 当 table 传递过来才执行如下操作
            col_count = self.table.columnCount()

            # 如果为空
            if not text_list_result:
                self.msg_display("msg0004")
                return
            # 数据校验写入
            if self.name_table == "permission_edit":
                if self.method_valid_permission_edit(col_count, text_list_result):
                    self.method_write_in_table_permission_edit(text_list_result)
            elif self.name_table == "report_visual_edit":
                if self.method_valid_report_visual_edit(col_count, text_list_result):
                    self.method_write_in_table_report_visual_edit(text_list_result)

    def method_valid_permission_edit(self, col_count, text_list_result):
        """校验数据 permission_edit

        :param col_count:列数
        :type col_count:int
        :param text_list_result:文字列表的结果
        :type text_list_result:list
        :return:True False
        :rtype:bool
        """
        # ['0=>Home', '1=>Navigation', '2=>NoPermission', '3=>A01', '4=>A02', '5=>A03']
        page_value = self.get_page_value_list(self.path)
        page_list = [item.split("=>")[0] for item in page_value]
        pages = ",".join(page_list)
        rls_value = self.get_rls_value_list(self.path)
        rls_value.insert(0, pages)

        for row_index, row_text in enumerate(text_list_result):
            col_list = row_text.split('|')
            # 列数是否符合
            if len(col_list) != col_count:
                self.msg_display("msg0001")
                return False

            for col_index, col in enumerate(col_list):
                # 不能为空
                if col == "":
                    value_dic = {"row": row_index + 1, "col": col_index + 1}
                    self.msg_display("msg0002", value_dic)
                    return False
                # 验证是否符合原数据
                if col_index > 2 and not set(col.split(",")) <= set(rls_value[col_index - 3].split(",")):  # 是否包含或等于
                    value_dic = {"row": row_index + 1, "col": col_index, "rls_value": rls_value[col_index - 3]}
                    self.msg_display("msg0003", value_dic)
                    return False
        return True

    def method_valid_report_visual_edit(self, col_count, text_list_result):
        """校验数据 report_visual_edit

        :param col_count:列数
        :type col_count:int
        :param text_list_result:文字列表的结果
        :type text_list_result:list
        :return:True False
        :rtype:bool
        """

        values = list(self.data_category.values())

        for row_index, row_text in enumerate(text_list_result):
            col_list = row_text.split('|')
            # 列数是否符合
            if len(col_list) != col_count:
                self.msg_display("msg0001")
                return False

            for col_index, col in enumerate(col_list):
                # 不能为空
                if col_index < 4 and col == "":
                    value_dic = {"row": row_index + 1, "col": col_index + 1}
                    self.msg_display("msg0002", value_dic)
                    return False
                # 验证数据类别
                if col_index == 2 and col not in values:
                    value_dic = {"row": row_index + 1, "col": col_index + 1, "rls_value": ",".join(values)}
                    self.msg_display("msg0003", value_dic)
                    return False
                # # 验证表格是否在原来的表格中
                # if col_index == 3 and col not in self.measure_table:
                #     value_dic = {"row": row_index + 1, "col": col_index + 1, "rls_value": ",".join(self.measure_table)}
                #     self.msg_display("msg0003", value_dic)
                #     return False
                # 验证复选框的选择状态
                if col_index == 3 and col not in ("True", "False", "true", "false", "TRUE", "FALSE"):
                    value_dic = {"row": row_index + 1, "col": col_index + 1}
                    self.msg_display("msg0005", value_dic)
                    return False
        return True

    def method_write_in_table_permission_edit(self, text_list_result):
        """数据写回 table_permission_edit

        :param text_list_result: 文本列表的结果
        :type text_list_result: list
        :return:None
        :rtype:None
        """
        row_count = len(text_list_result)
        self.table.setRowCount(row_count)
        for row, row_item in enumerate(text_list_result):
            for col, item in enumerate(row_item.split('|')):
                cell = QTableWidgetItem(item)
                cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
                self.table.setItem(row, col, cell)

    def method_write_in_table_report_visual_edit(self, text_list_result):
        """数据写回 table_report_visual_edit

        :param text_list_result: 文本列表的结果
        :type text_list_result: list
        :return:None
        :rtype:None
        """
        row_count = len(text_list_result)
        self.table.setRowCount(row_count)
        for row, row_item in enumerate(text_list_result):
            for col, item in enumerate(row_item.split('|')):
                if col == 3:
                    cell = QTableWidgetItem()
                    if item in ("True", "true", "TRUE"):
                        # Unchecked = ...  # type: Qt.CheckState
                        # PartiallyChecked = ...  # type: Qt.CheckState
                        # Checked = ...  # type: Qt.CheckState
                        cell.setCheckState(Qt.CheckState.Checked)
                    else:
                        cell.setCheckState(Qt.CheckState.Unchecked)
                else:
                    cell = QTableWidgetItem(item)
                    cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
                self.table.setItem(row, col, cell)


# if __name__ == '__main__':
#     text_list = ["第一行", "第二行", "第三行", "第四行"]
#     app = QApplication(sys.argv)
#     demo_widget = Text(text_list)
#     demo_widget.show()
#     sys.exit(app.exec())