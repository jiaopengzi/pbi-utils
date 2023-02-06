# -*- encoding: utf-8 -*-
"""
@File           :   ui_05_permission_edit.py
@Time           :   2022-11-10, 周四, 18:0
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   权限编辑
"""

from PySide6.QtCore import Qt
from PySide6.QtGui import QCursor
from PySide6.QtWidgets import (QFileDialog, QHBoxLayout, QLineEdit,
                               QMenu, QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget)

from configfiles import DISPLAY_CONFIG
from ui.dialog_text import Text
from ui.method import UiMethod, ValidatedItemDelegate
from ui.multi_line_edit import MultiLineEdit
from utils.threads_jpz import (ThreadAddJsonPermissionEdit, ThreadLoadJsonPermissionEdit, ThreadSaveJsonPermissionEdit)
from utils.validators import Validator


class PermissionEdit(UiMethod):
    """权限编辑
    """
    ui_05 = "ui_05"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_permission_edit(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_05_dic = DISPLAY_CONFIG[self.ui_05]
        for key in self.ui_05_dic:
            setattr(self, key, key)

        self.widget_05 = QWidget()
        self.ui_05_layout = QVBoxLayout()
        # 添加子布局
        self.ui_05_layout.addLayout(self.ui_permission_edit_layout_v1())
        self.ui_05_layout.addLayout(self.ui_permission_edit_layout_v2())
        self.widget_05.setLayout(self.ui_05_layout)
        self.progress_bar_display()
        return self.widget_05

    def ui_permission_edit_layout_v1(self):
        """布局 v1
        
        :return: layout
        :rtype: QHBoxLayout
        """

        layout = QHBoxLayout()
        Line_edit_layout0 = self.Line_edit_layout_x(self.ui_05, self.QLabel_config, self.QLineEdit_config)
        layout.addLayout(Line_edit_layout0)

        btn1 = self.button_x(self.ui_05, self.QPushButton_choose, )
        btn2 = self.button_x(self.ui_05, self.QPushButton_add, )
        btn3 = self.button_x(self.ui_05, self.QPushButton_save, )
        btn1.clicked.connect(self.btn_select_clicked_permission_edit)
        btn2.clicked.connect(self.btn_add_clicked_permission_edit)
        btn3.clicked.connect(self.btn_save_clicked_permission_edit)
        layout.addWidget(btn1)
        layout.addWidget(btn2)
        layout.addWidget(btn3)

        return layout

    def ui_permission_edit_layout_v2(self):
        """表格
        
        :return:layout
        :rtypeQHBoxLayout
        """

        layout = QHBoxLayout()
        table = QTableWidget()
        table.setObjectName(f"{self.ui_05}_{self.QTableWidget_table}")
        # 开启右键
        table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        table.customContextMenuRequested.connect(self.ui_05_table_right_menu)
        layout.addWidget(table)
        return layout

    def ui_05_table_right_menu(self):
        """开启右键
        
        :return:None 
        :rtype:None
        """

        le = self.findChild(QLineEdit, f"{self.ui_05}_{self.QLineEdit_config}")
        le_str = le.text()
        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_05_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0601", msg)
            le.setFocus()
            return
        table = self.findChild(QTableWidget, f"{self.ui_05}_{self.QTableWidget_table}")

        if selected_items := table.selectedItems():
            self.ui_05_table_right_menu_action(table, selected_items, le_str)

    def ui_05_table_right_menu_action(self, table, selected_items, file):
        """绑定右键动作
        
        :param table: 表格对象
        :type table: object
        :param selected_items: 选择的元素列表
        :type selected_items: list
        :param file: 文件路径
        :type file: str
        :return: None
        :rtype: None
        """
        menu = QMenu(table)
        action_delete = menu.addAction(self.ui_05_dic[self.QAction_del]['display'])
        action_edit = menu.addAction(self.ui_05_dic[self.QAction_edit]['display'])
        action_init = menu.addAction(self.ui_05_dic[self.QAction_init]['display'])
        action_multi = menu.addAction(self.ui_05_dic[self.QAction_multi]['display'])
        action = menu.exec(QCursor.pos())

        if action == action_delete:
            for item in selected_items:
                table.removeRow(item.row())

        if action == action_edit:
            item = selected_items[0]
            row = item.row()
            col = item.column()
            if col == 2:
                self.ui_05_table_right_action_edit_multi_text(table, row, col)
            if col == 3:
                self.ui_05_table_right_action_edit_multi_choice_page(file, table, row, col)
            if col > 3:
                self.ui_05_table_right_action_edit_multi_choice_not_page(file, table, row, col)

        if action == action_init:
            row_list = [item.row() for item in selected_items]
            # 1、在json 字典中 PowerBIUsers 中添加一个空白 user 信息，并把page和rls自动补全 2、加载到表格中
            self.thread = ThreadAddJsonPermissionEdit(file, table, row_list)
            self.thread.status.connect(self.add_json_permissionedit_callback)
            self.thread.start()
            # 按钮状态
            if self.thread.isRunning():
                self.btn_status_permission_edit(False)

        if action == action_multi:
            self.ui_05_table_right_action_multi(table, file)

    @staticmethod
    def ui_05_table_right_action_edit_multi_text(table, row, col):
        """右键多行编辑

        :param table:表格对象
        :type table:QTableWidget
        :param row:行号
        :type row:int
        :param col:列号
        :type col:int
        :return:None
        :rtype:None
        """
        text_list = table.item(row, col).text().split(',')
        text = Text(textList=text_list)
        text.exec()
        if text_list_result := text.text_list_result:
            text_line = ",".join(text_list_result)
            cell = QTableWidgetItem(text_line)
            cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
            table.setItem(row, col, cell)

    def ui_05_table_right_action_edit_multi_choice_page(self, file, table, row, col):
        """右键多选选择-页面

        :param file: 文件路径
        :type file: str
        :param table: 表格对象
        :type table: QTableWidget
        :param row: 行号
        :type row: int
        :param col: 列号
        :type col: int
        :return: None
        :rtype: None
        """
        delimiter = "=>"
        list_left = self.get_page_value_list(file)
        list_right = table.item(row, col).text().split(',')

        list_right_new = []
        list_left_ordinal = [(page.split(delimiter)[0], page.split(delimiter)[1]) for page in list_left]
        for item in list_right:
            for ordinal, display_name in list_left_ordinal:
                if ordinal == item:
                    list_right_new.append(ordinal + delimiter + display_name)
                    break

        multi = MultiLineEdit(list_left, list_right_new)
        multi.exec()
        list_right = multi.list_right
        if not list_right:
            self.msg_display("msg0602")
        list_right = [text.split("=>")[0] for text in list_right]
        cell = QTableWidgetItem(",".join(list_right))
        cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
        table.setItem(row, col, cell)

    def ui_05_table_right_action_edit_multi_choice_not_page(self, file, table, row, col):
        """右键多项选择-非页面

        :param file: 文件路径
        :type file: str
        :param table: 表格对象
        :type table: QTableWidget
        :param row: 行号
        :type row: int
        :param col: 列号
        :type col: int
        :return: None
        :rtype: None
        """

        rls_value = self.get_rls_value_list(file)
        list_left = rls_value[col - 4].split(',')
        list_right = table.item(row, col).text().split(',')
        multi = MultiLineEdit(list_left, list_right)
        multi.exec()
        list_right = multi.list_right
        if not list_right:
            self.msg_display("msg0603")
        cell = QTableWidgetItem(",".join(list_right))
        cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
        table.setItem(row, col, cell)

    @staticmethod
    def ui_05_table_right_action_multi(table, file):
        """右键多行拜编辑

        :param table:表格对象
        :type table:QTableWidget
        :param file:文件路径
        :type file:str
        :return:None
        :rtype:None
        """
        row_count = table.rowCount()
        col_count = table.columnCount()
        row_all = []
        for row in range(row_count):
            row_x = []
            for col in range(col_count):
                cell = table.item(row, col).text()
                row_x.append(cell)
            row_all.append("|".join(row_x))
        text = Text(row_all, file, table, "permission_edit")
        text.exec()

    def btn_select_clicked_permission_edit(self):
        """选择按钮

        :return:None
        :rtype: None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.json")
        le = self.findChild(QLineEdit, f"{self.ui_05}_{self.QLineEdit_config}")
        le.setText(files)
        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(files)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_05_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0604", msg)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_05}_{self.QTableWidget_table}")
        self.thread = ThreadLoadJsonPermissionEdit(files, table)
        self.thread.status.connect(self.load_json_permissionedit_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_status_permission_edit(False)

    def load_json_permissionedit_callback(self, status):
        """加载 json 回调函数

        :param status: 0 或 1
        :type status: int
        :return: None
        :rtype: None
        """

        if not status:
            le = self.findChild(QLineEdit, f"{self.ui_05}_{self.QLineEdit_config}")
            le.setText("")
            self.msg_display("msg0605")
            le.setFocus()
            self.btn_status_permission_edit(True)
            return
        # 标题宽度设置
        table = self.findChild(QTableWidget, f"{self.ui_05}_{self.QTableWidget_table}")
        # print("table")
        for id in range(table.columnCount()):
            self.set_table_column_width_resize_to_contents_interactive(table, id)
        # 输入校验
        cols = table.columnCount()
        col_not_edit_list = range(cols)[2:]  # 不能编辑的列
        valid = ValidatedItemDelegate()
        valid.col_not_edit_list = col_not_edit_list
        table.setItemDelegate(valid)
        # 按钮状态
        self.btn_status_permission_edit(True)

    def btn_add_clicked_permission_edit(self):
        """新增按钮

        :return:None
        :rtype: None
        """

        le = self.findChild(QLineEdit, f"{self.ui_05}_{self.QLineEdit_config}")
        le_str = le.text()
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_05_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0606", msg)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_05}_{self.QTableWidget_table}")
        # 1、在json 字典中 PowerBIUsers 中添加一个空白 user 信息，并把page和rls自动补全 2、加载到表格中
        self.thread = ThreadAddJsonPermissionEdit(le_str, table)
        self.thread.status.connect(self.add_json_permissionedit_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_status_permission_edit(False)

    def add_json_permissionedit_callback(self, status):
        """新增按钮回调函数

        :param status: 0 或 1
        :type status: int
        :return: None
        :rtype: None
        """

        if not status:
            self.msg_display("msg0607")
        self.btn_status_permission_edit(True)

    def btn_save_clicked_permission_edit(self):
        """保存

        :return:None
        :rtype: None
        """

        le = self.findChild(QLineEdit, f"{self.ui_05}_{self.QLineEdit_config}")
        le_str = le.text()
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_05_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0608", msg)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_05}_{self.QTableWidget_table}")
        self.thread = ThreadSaveJsonPermissionEdit(le_str, table)
        self.thread.status.connect(self.save_json_permissionedit_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_status_permission_edit(True)

    def save_json_permissionedit_callback(self, status):
        """保存 json 回调函数

        :param status: 0 或 1 # {"status": 0, "row": row + 1, "col": col + 1, "error": "blank"}
        :type status: dict
        :return: None
        :rtype: None
        """
        if not status["status"] and "error" not in status:
            self.msg_display("msg0609")

        elif not status["status"] and status["error"] == "blank":
            msg = {"row": status["row"], "col": status["col"]}
            self.msg_display("msg0610", msg)

        elif status["status"]:
            self.msg_display("msg0611", is_info=True)

        self.btn_status_permission_edit(True)

    def btn_status_permission_edit(self, boolean):
        """按钮状态

        :param boolean: True or False
        :type boolean: bool
        :return: None
        :rtype: None
        """
        self.btn_enabled_status(f"{self.ui_05}_{self.QPushButton_choose}", boolean)
        self.btn_enabled_status(f"{self.ui_05}_{self.QPushButton_add}", boolean)
        self.btn_enabled_status(f"{self.ui_05}_{self.QPushButton_save}", boolean)