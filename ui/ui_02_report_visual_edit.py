# -*- encoding: utf-8 -*-
"""
@File           :   ui_02_report_visual_edit.py
@Time           :   2022-11-10, 周四, 17:14
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   度量值模板ui
"""

from PySide6.QtCore import Qt
from PySide6.QtGui import QCursor
from PySide6.QtWidgets import QFileDialog, QHBoxLayout, QLabel, QLineEdit, QMenu, QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget

from configfiles import DATA_CATEGORY, DISPLAY_CONFIG, REPORT_VISUAL_COLUMN_NAME
from ui.dialog_text import Text
from ui.method import UiMethod, ValidatedItemDelegate
from utils.methods import read_json
from utils.threads_jpz import ThreadLoadJsonReportVisualEdit, ThreadSaveJsonReportVisualEdit
from utils.validators import Validator


class ReportVisualEdit(UiMethod):
    """度量值模板ui
    """
    ui_02 = "ui_02"

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_report_visual_edit(self):
        """ui

        :return:返回中间页面
        :rtype:QWidget
        """
        self.ui_02_dic = DISPLAY_CONFIG[self.ui_02]
        for key in self.ui_02_dic:
            setattr(self, key, key)

        self.widget_02 = QWidget()
        self.ui_02_layout = QVBoxLayout()
        # 添加子布局
        self.ui_02_layout.addLayout(self.ui_report_visual_edit_layout_v1())
        self.ui_02_layout.addLayout(self.ui_report_visual_edit_layout_v2())
        self.ui_02_layout.addLayout(self.init_layout_ui_02_3_label())
        self.widget_02.setLayout(self.ui_02_layout)
        self.progress_bar_display()
        return self.widget_02

    def ui_report_visual_edit_layout_v1(self):
        """布局 v1

        :return: layout
        :rtype: QHBoxLayout
        """

        layout = QHBoxLayout()
        Line_edit_layout0 = self.Line_edit_layout_x(self.ui_02, self.QLabel_config, self.QLineEdit_config)
        layout.addLayout(Line_edit_layout0)

        btn1 = self.button_x(self.ui_02, self.QPushButton_choose)
        btn2 = self.button_x(self.ui_02, self.QPushButton_add)
        btn3 = self.button_x(self.ui_02, self.QPushButton_save)
        btn1.clicked.connect(self.btn1_clicked_report_visual_edit_choose)
        btn2.clicked.connect(self.btn2_clicked_report_visual_edit_add)
        btn3.clicked.connect(self.btn3_clicked_report_visual_edit_save)
        layout.addWidget(btn1)
        layout.addWidget(btn2)
        layout.addWidget(btn3)

        return layout

    def ui_report_visual_edit_layout_v2(self):
        """第 2 行 表格

        :return: table_layout
        :rtype: QHBoxLayout
        """
        cols = len(REPORT_VISUAL_COLUMN_NAME)
        table_layout = QHBoxLayout()
        # table_layout.setObjectName("layout_table")

        table = QTableWidget(0, cols)
        table.setObjectName(f"{self.ui_02}_{self.QTableWidget_table}")
        for id, name in enumerate(REPORT_VISUAL_COLUMN_NAME):
            item = QTableWidgetItem()
            item.setText(REPORT_VISUAL_COLUMN_NAME[name])
            table.setHorizontalHeaderItem(id, item)
            self.set_table_column_width_resize_to_contents_interactive(table, id)  # 设置宽度
        # 开启右键
        table.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        table.customContextMenuRequested.connect(self.ui_02_table_right_menu)
        # 输入校验
        valid = ValidatedItemDelegate()
        col_reg_list = [
                {"cols": [0, 5], "reg": "^\S*$"}  # 不能有空格
        ]
        valid.col_reg_list = col_reg_list
        valid.col_not_edit_list = [2, 3]  # 不能编辑，但能选中
        table.setItemDelegate(valid)

        table_layout.addWidget(table)
        return table_layout

    def ui_02_table_right_menu(self):
        """表格开启右键

        :return:None
        :rtype:None
        """

        le = self.findChild(QLineEdit, f"{self.ui_02}_{self.QLineEdit_config}")
        le_str = le.text()
        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_02_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0301", msg)
            le.setFocus()
            return
        table = self.findChild(QTableWidget, f"{self.ui_02}_{self.QTableWidget_table}")

        if selected_items := table.selectedItems():
            self.ui_02_table_right_menu_action(table, selected_items, le_str)

    def ui_02_table_right_menu_action(self, table, selected_items, file):
        """右键的按钮绑定函数

        :param table:表格对象
        :type table:object
        :param selected_items:选择的元素列表
        :type selected_items:list
        :param file:文件路径
        :type file:str
        :return:None
        :rtype:None
        """

        menu = QMenu(table)
        action_delete = menu.addAction(self.ui_02_dic[self.QAction_del]['display'])
        action_edit = menu.addAction(self.ui_02_dic[self.QAction_edit]['display'])
        action_multi = menu.addAction(self.ui_02_dic[self.QAction_multi]['display'])
        action = menu.exec(QCursor.pos())

        if action == action_delete:
            for item in selected_items:
                table.removeRow(item.row())

        if action == action_edit:
            self.action_edit_method(selected_items, table)
        if action == action_multi:
            self.ui_02_table_right_action_multi(table, file)

    def action_edit_method(self, selected_items: list, table: object) -> None:
        """编辑抽取的方法

        :param selected_items:选择项目的列表
        :param table:表格对象
        :return:None
        """
        item = selected_items[0]
        row = item.row()
        col = item.column()
        keys = list(DATA_CATEGORY.keys())
        values = list(DATA_CATEGORY.values())
        if col == 2:
            radio_key = keys[values.index(item.text())] if item.text() else "Uncategorized"
            self.radio_choice(table, row, col, DATA_CATEGORY, radio_key, is_display=True)

    @staticmethod
    def ui_02_get_all_tables_in_json(file):
        """从 config.json 中获取所有表格

        :param file:文件路径
        :type file:str
        :return:measure_table
        :rtype:list
        """
        json_dic = read_json(file)
        measure_table = json_dic["MeasureTable"]
        data_table = json_dic["TableColumns"]
        for t in data_table:
            if t not in measure_table:
                measure_table.append(t)
        return measure_table

    @staticmethod
    def ui_02_table_right_action_multi(table, file):
        """右键多行编辑

        :param table: 表格对象
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
                if col == 3:
                    bool_ = table.item(row, col).checkState() == Qt.CheckState.Checked
                    cell = str(bool_)
                row_x.append(cell)
            row_all.append("|".join(row_x))
        # measure_table = self.ui_02_get_all_tables_in_json(file)
        # text = Text(row_all, file, table, "report_visual_edit", DATA_CATEGORY, measure_table)
        text = Text(row_all, file, table, "report_visual_edit", DATA_CATEGORY)
        text.exec()

    def btn1_clicked_report_visual_edit_choose(self):
        """选择按钮

        :return:None
        :rtype:None
        """

        file_diglog = QFileDialog()
        files, _ = file_diglog.getOpenFileName(filter="*.json")
        le = self.findChild(QLineEdit, f"{self.ui_02}_{self.QLineEdit_config}")
        le.setText(files)

        # 校验器
        valid = Validator()
        le_is_json = valid.is_json(files)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_02_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0304", msg)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_02}_{self.QTableWidget_table}")

        self.thread = ThreadLoadJsonReportVisualEdit(files, table)
        self.thread.status.connect(self.load_json_report_visual_edit_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_status_report_visual_edit(False)

    def load_json_report_visual_edit_callback(self, status):
        """加载json的回调函数

        :param status: 0 或者 1
        :type status:int
        :return:None
        :rtype:None
        """
        if not status:
            le = self.findChild(QLineEdit, f"{self.ui_02}_{self.QLineEdit_config}")
            le.setText("")
            self.msg_display("msg0305")
            self.btn_status_report_visual_edit(True)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_02}_{self.QTableWidget_table}")
        cols = table.columnCount()
        for col in range(cols):
            self.set_table_column_width_resize_to_contents_interactive(table, col)
        # 按钮状态
        self.btn_status_report_visual_edit(True)

    def btn2_clicked_report_visual_edit_add(self):
        """新增按钮

        :return:None
        :rtype:None
        """
        le = self.findChild(QLineEdit, f"{self.ui_02}_{self.QLineEdit_config}")
        le_str = le.text()
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_02_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0306", msg)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_02}_{self.QTableWidget_table}")
        table.insertRow(0)
        cols = table.columnCount()
        for col in range(cols):
            cell = QTableWidgetItem()
            if col == 3:
                cell.setCheckState(Qt.CheckState.Unchecked)
            table.setItem(0, col, cell)

    def btn3_clicked_report_visual_edit_save(self):
        """保存按钮

        :return:None
        :rtype:None
        """
        le = self.findChild(QLineEdit, f"{self.ui_02}_{self.QLineEdit_config}")
        le_str = le.text()
        valid = Validator()
        le_is_json = valid.is_json(le_str)
        if not le_is_json[0]:
            msg = {"value": f"{self.ui_02_dic[self.QLabel_config]['display']}:{le_is_json[1]}"}
            self.msg_display("msg0308", msg)
            le.setFocus()
            return

        table = self.findChild(QTableWidget, f"{self.ui_02}_{self.QTableWidget_table}")
        self.thread = ThreadSaveJsonReportVisualEdit(le_str, table)
        self.thread.status.connect(self.save_json_report_visual_edit_callback)
        self.thread.start()
        # 按钮状态
        if self.thread.isRunning():
            self.btn_status_report_visual_edit(True)

    def save_json_report_visual_edit_callback(self, status):
        """保存按钮的回调函数

        :param status: {"status":0,"row":1,"col":2}
        :type status:dict
        :return:None
        :rtype:None
        """
        if status["status"]:
            self.msg_display("msg0312", is_info=True)

        elif "error" in status and status["error"] == "blank":
            msg = {"row": status["row"], "col": status["col"]}
            self.msg_display("msg0310", msg)

        elif "error" in status and status["error"] == "repeat":
            msg = {"row_old": status["row_old"], "row": status["row"], "col": status["col"]}
            self.msg_display("msg0311", msg)

        elif "error" in status and status["error"] == "PermissionError":
            self.msg_display("msg1303")  # 完成提示
        elif "error" not in status:
            self.msg_display("msg0309")

        self.btn_status_report_visual_edit(True)

    def btn_status_report_visual_edit(self, boolean):
        """按钮状态

        :param boolean:True False
        :type boolean:bool
        :return:None
        :rtype:None
        """
        self.btn_enabled_status(f"{self.ui_02}_{self.QPushButton_choose}", boolean)
        self.btn_enabled_status(f"{self.ui_02}_{self.QPushButton_add}", boolean)
        self.btn_enabled_status(f"{self.ui_02}_{self.QPushButton_save}", boolean)

    def init_layout_ui_02_3_label(self):
        """第 3 行 底部说明

        :return: label_layout
        :rtype: QHBoxLayout
        """

        label_layout = QHBoxLayout()
        label = QLabel(self.ui_02_dic[self.QLabel_description]['display'])  # 输入框
        label.setObjectName(f"{self.ui_02_dic}_{self.QLabel_description}")
        # label.setFixedHeight(64)
        # label.setStyleSheet("border: 1px solid red;")  # 通过边框颜色来查看布局避免错误
        label_layout.addWidget(label)
        return label_layout