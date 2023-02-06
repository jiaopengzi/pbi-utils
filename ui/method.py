# -*- encoding: utf-8 -*-
"""
@File           :   method.py
@Time           :   2022-11-10, 周四, 15:17
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   ui的方法集合
"""

import os
from string import Template

from PySide6.QtCore import QRegularExpression, QSize, Qt
from PySide6.QtGui import QColor, QFont, QIcon, QPalette, QRegularExpressionValidator
from PySide6.QtWidgets import (QComboBox, QCompleter, QHBoxLayout,
                               QHeaderView, QLabel, QLineEdit, QMessageBox, QProgressBar, QPushButton, QStyledItemDelegate, QTableWidget,
                               QTableWidgetItem, QWidget)

from config import language_text
from configfiles import DISPLAY_CONFIG, EN_US, MSG, PERMISSION_EDIT_COLUMN_NAME, ZH_CN
from ui.dialog_radio import Radio
from utils.methods import read_json


class ValidatedItemDelegate(QStyledItemDelegate):
    """校验委托

    """
    col_reg_list = []  # {"cols": [], "reg": ""},需要使用的正则的列
    col_not_edit_list = []  # [0,1,3] ,可以先择，不可以编辑

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def createEditor(self, widget, option, index):
        """createEditor 重写，编辑时校验,参考源码

        """
        if not index.isValid():
            return
        if self.col_reg_list:
            for dic in self.col_reg_list:
                if index.column() in dic["cols"]:  # only on the cells in the first column
                    editor = QLineEdit(widget)
                    # 数字列表，且数组不大于三位数
                    reg = QRegularExpression(dic["reg"])
                    regVal = QRegularExpressionValidator(reg)
                    editor.setValidator(regVal)
                    return editor
        # 不可编辑但可以选中
        if self.col_not_edit_list and index.column() in self.col_not_edit_list:
            return
        return super(ValidatedItemDelegate, self).createEditor(widget, option, index)  # 默认集成无需校验


class UiMethod(QWidget):
    """QWidget 通用方法

    """

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def msg_display(self, msgxxxx, tmp_dic=None, is_info=False):
        """消息提示框格式化显示，根据消息文档配置

        :param msgxxxx:消息编号名称
        :type msgxxxx:str
        :param tmp_dic:消息模板字典
        :type tmp_dic:dict
        :param is_info:消息提示类型为信息
        :type is_info:bool
        :return:None
        :rtype:None
        """
        dic = MSG[msgxxxx]
        if tmp_dic is None:
            msg = dic["msg"]
        else:
            value = Template(dic["msg"])
            msg = value.substitute(tmp_dic)
        if is_info:
            QMessageBox.information(self, dic["title"], msg, QMessageBox.StandardButton.Ok)
        else:
            QMessageBox.warning(self, dic["title"], msg, QMessageBox.StandardButton.Ok)

    @staticmethod
    def radio_choice(table, row, column, dict, radio_key, is_display=False):
        """单选按钮选择后赋值给目标

        :param table:目标表格
        :type table:object
        :param row:行号
        :type row:int
        :param column:列号
        :type column:int
        :param dict:可以选择的字典
        :type dict:dict
        :param radio_key:默认选择的值的key
        :type radio_key:int
        :param is_display:是否按照字典的值来显示
        :type is_display:bool
        :return:None
        :rtype:None
        """
        radio = Radio(dict, radio_key)
        radio.exec()
        new_text = radio.radio_result

        # 根据原有单选框变化更新赋值
        new_cell = QTableWidgetItem()
        if is_display:
            new_cell.setText(dict[new_text])
        else:
            new_cell.setText(new_text)
        new_cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
        table.setItem(row, column, new_cell)
        # new.setFlags(Qt.ItemFlag.ItemIsEnabled)  # 设置回不可编辑

    def btn_enabled_status(self, objectname, status):
        """
        按钮状态函数 status : True | False
        """
        btn1 = self.findChild(QPushButton, objectname)
        btn1.setEnabled(status)

    @staticmethod
    def has_file_type(file_root_path, suffix):
        """判断是否有该后缀的文件类型

        :param file_root_path:文件的路径
        :type file_root_path:str
        :param suffix:后缀字符串
        :type suffix:str
        :return:True False
        :rtype:bool
        """
        return any((file.endswith(suffix) for file in os.listdir(file_root_path)))

    def combobox_update(self, combo_name, json_path, key_l1, key_l2=None):
        """更新 combo 列表项目 加载模型中的表

        :param combo_name: 名称
        :type combo_name: str
        :param json_path:json文件路径
        :type json_path:str
        :param key_l1:第一层的key
        :type key_l1:str
        :param key_l2:第二层的key, 默认 None
        :type key_l2:str
        :return:None
        :rtype:None
        """

        combo = self.findChild(QComboBox, combo_name)
        dic = read_json(json_path)
        combo.clear()
        if key_l2:
            for item in dic[key_l1][key_l2]:
                combo.addItem(item)
        else:
            for item in dic[key_l1]:
                combo.addItem(item)

    def auto_completer_text(self, items_list, count):
        """自动补全=>自定义文本 list

        :param items_list:["焦棚子", "jiaopengzi", "JPZ"]
        :type items_list:list
        :param count:显示的项目数量
        :type count:int
        :return:QCompleter
        :rtype:QCompleter
        """

        completer = QCompleter(items_list)
        self.auto_completer_public(completer, count)
        return completer

    @staticmethod
    def auto_completer_public(completer, count):
        """抽取的自动补全函数

        :param completer: QCompleter
        :type completer: QCompleter
        :param count:显示的项目数量
        :type count:int
        :return: None
        :rtype: None
        """

        # 设置匹配模式有三种
        # Qt.MatchFlag.MatchStartsWith 开头匹配（默认）
        # Qt.MatchFlag.MatchContains 内容匹配
        # Qt.MatchFlag.MatchEndsWith 结尾匹配
        completer.setFilterMode(Qt.MatchFlag.MatchContains)

        # 设置显示元素数量
        completer.setMaxVisibleItems(count)

        # 大小写
        # completer.setCaseSensitivity(Qt.CaseSensitivity.CaseInsensitive)  # 忽略大小写
        completer.setCaseSensitivity(Qt.CaseSensitivity.CaseSensitive)  # 大小写敏感

        # 设置补全模式有三种：
        # QCompleter.CompletionMode.PopupCompletion 弹出选项补全（默认）
        # QCompleter.CompletionMode.InlineCompletion 行内显示补全
        # QCompleter.CompletionMode.UnfilteredPopupCompletion 全显选项补全
        completer.setCompletionMode(QCompleter.CompletionMode.PopupCompletion)

    # def resizeEvent(self, event):
    #     """
    #     自适应尺寸
    #     """
    #     self.tab.setFixedSize(self.size())
    #     label1 = self.findChild(QLabel, "view2_1_label")
    #     table2 = self.findChild(QTableWidget, "view2_2_table")
    #     label3 = self.findChild(QLabel, "view2_3_label")
    #     h1 = label1.height()
    #     h3 = label3.height()
    #     h2 = self.height()-3*h1-h3  # 3倍
    #     table2.setFixedHeight(h2)
    #     super().resizeEvent(event)

    def Line_edit_layout_x(self, ui_x, label_name, lineedit_name, minimum_width=None, reg_str=None):
        """包含标签的单行布局

        :param ui_x: ui视图
        :type ui_x:str
        :param label_name:标签对象名称
        :type label_name:str
        :param lineedit_name:编辑框的对象名称
        :type lineedit_name:str
        :param minimum_width:最小宽度, 默认 None
        :type minimum_width:int
        :param reg_str:正则字符串，默认None
        :type reg_str:str
        :return:编辑框布局
        :rtype:QHBoxLayout
        """

        result = QHBoxLayout()
        label = QLabel(DISPLAY_CONFIG[ui_x][label_name]["display"])
        label.setObjectName(f"{ui_x}_{label_name}")
        if language_text == ZH_CN:
            label.setMinimumWidth(80)
        elif language_text == EN_US:
            label.setMinimumWidth(120)

        # # 设置字符间距
        # font_label = label.font()
        # font_label.setLetterSpacing(QFont.SpacingType.AbsoluteSpacing, 0.6)
        # label.setFont(font_label)

        # 文本编辑框
        line_edit = QLineEdit()
        line_edit.setObjectName(f"{ui_x}_{lineedit_name}")
        line_edit.setPlaceholderText(DISPLAY_CONFIG[ui_x][lineedit_name]["placeholder"])
        # self.set_text_color(line_edit)
        # line_edit.setPalette(QPalette.PlaceholderText(), QColor(255, 0, 255, 255))

        # # 设置字符间距
        # font_edit = line_edit.font()
        # font_edit.setLetterSpacing(QFont.SpacingType.AbsoluteSpacing, 0.6)
        # line_edit.setFont(font_edit)

        if minimum_width:
            line_edit.setMinimumWidth(minimum_width)
        if reg_str:
            reg = QRegularExpression(reg_str)
            regVal = QRegularExpressionValidator(reg)
            # 将校验器和输入框绑定
            line_edit.setValidator(regVal)
        result.addWidget(label)
        result.addWidget(line_edit)

        line_edit.textChanged.connect(lambda: self.line_edit_text_changed_color(f"{ui_x}_{lineedit_name}"))
        self.set_text_color(line_edit)
        return result

    def line_edit_text_changed_color(self, object_name: str) -> None:
        """文本编辑框文字变化是设置颜色

        :param object_name:文本编辑框对象的名称
        :type object_name:str
        :return:None
        :rtype:None
        """
        le = self.findChild(QLineEdit, object_name)
        self.set_text_color(le)

    @staticmethod
    def button_x(ui_x, btn_name):
        """自定义按钮对象

        :param ui_x: ui视图
        :type ui_x:str
        :param btn_name:按钮对象名称
        :type btn_name:str
        :return:QPushButton
        :rtype:QPushButton
        """

        result = QPushButton(DISPLAY_CONFIG[ui_x][btn_name]["display"])
        result.setObjectName(f"{ui_x}_{btn_name}")
        # 设置字符间距
        font_result = result.font()
        font_result.setLetterSpacing(QFont.SpacingType.AbsoluteSpacing, 0.6)
        result.setFont(font_result)
        icon = QIcon()
        icon.addFile(f':/icon/image/{DISPLAY_CONFIG[ui_x][btn_name]["icon"]}', QSize(), QIcon.Normal, QIcon.Off)
        result.setIcon(icon)
        # result.clicked.connect(getattr(self, f"button_{dic[index]['name']}_clicked"))
        return result

    def combobox_layout_x(self, ui_x, label_name, combo_name,
                          currentTextChanged_fun=None, minimum_width=None, item_list=None, current_text=None) -> object:
        """自定义下拉框布局

        :param ui_x: ui视图
        :type ui_x:str
        :param label_name: 标签对象名称
        :type label_name: str
        :param combo_name: 下拉框对象名称
        :type combo_name: str
        :param currentTextChanged_fun: 当前值变化的时候的槽函数名称，默认 None
        :type currentTextChanged_fun:object
        :param minimum_width:最小宽度
        :type minimum_width:int
        :param item_list:下拉框的数据源列表
        :type item_list:list
        :param current_text:当前的值
        :type current_text:str
        :return:combo的布局
        :rtype:QHBoxLayout
        """

        combo_layout = QHBoxLayout()
        label = QLabel(DISPLAY_CONFIG[ui_x][label_name]["display"])
        label.setObjectName(f"{ui_x}_{label_name}")
        if language_text == ZH_CN:
            label.setMinimumWidth(80)
        elif language_text == EN_US:
            label.setMinimumWidth(120)

        # # 设置字符间距
        # font_label = label.font()
        # font_label.setLetterSpacing(QFont.SpacingType.AbsoluteSpacing, 0.6)
        # label.setFont(font_label)

        # 下拉框
        combo = QComboBox()
        combo.setObjectName(f"{ui_x}_{combo_name}")
        combo.setPlaceholderText(DISPLAY_CONFIG[ui_x][combo_name]["placeholder"])
        combo.setMinimumWidth(300)

        # 添加选项
        if item_list:
            for item in item_list:
                combo.addItem(item)
        # 设置当前项目
        if current_text:
            combo.setCurrentText(current_text)

        # # 设置字符间距
        # font_combo = combo.font()
        # font_combo.setLetterSpacing(QFont.SpacingType.AbsoluteSpacing, 0.6)
        # combo.setFont(font_combo)

        if minimum_width:
            combo.setMinimumWidth(minimum_width)
        if currentTextChanged_fun:
            combo.currentTextChanged.connect(currentTextChanged_fun)
        else:
            combo.currentTextChanged.connect(lambda: self.combo_text_changed_color(f"{ui_x}_{combo_name}"))
        combo_layout.addWidget(label)
        combo_layout.addWidget(combo)
        combo_layout.addStretch(1)
        self.set_text_color(combo)
        return combo_layout

    def combo_text_changed_color(self, object_name: str) -> None:
        """下拉框框文字变化是设置颜色

        :param object_name:下拉框对象的名称
        :type object_name:str
        :return:None
        :rtype:None
        """
        combo = self.findChild(QComboBox, object_name)
        self.set_text_color(combo)

    @staticmethod
    def get_permission_edit_column_name(path):
        """获取 permission_edit_column_name

        :param path: json路径
        :type path:str
        :return:返回名称的字典
        :rtype:dict
        """

        dic_json = read_json(path)
        dic_list = dic_json["PermissionList"]
        role_page = {"reportPage": "页面序号"}
        if language_text == EN_US:
            role_page = {"reportPage": "ordinal"}
        role_rls = {dic["name"]: dic["tableColumn"] for dic in dic_list}
        return {**PERMISSION_EDIT_COLUMN_NAME, **role_page, **role_rls}

    @staticmethod
    def get_page_value_list(path):
        """获取页面的值的列表

        :param path: config.json路径
        :type path:str
        :return:页面编号和名称组合列表ordinal=>displayName
        :rtype:list
        """
        dic_json = read_json(path)
        dic_page = dic_json["ReportPages"]
        return [f'{dic["ordinal"]}=>{dic["displayName"]}' for dic in dic_page]

    @staticmethod
    def get_rls_value_list(path):
        """获取 rls 值的列表

        :param path: config.json路径
        :type path:str
        :return:rls value list
        :rtype:list
        """
        dic_json = read_json(path)
        dic_perm = dic_json["PermissionList"]
        return [",".join(dic["value"]) for dic in dic_perm]

    def progress_bar_display(self, progress_value=None):
        """进度条显示的方法

        :param progress_value:进度条的值，默认  None，则不显示
        :type progress_value:int
        :return:None
        :rtype:None
        """
        qb = self.findChild(QProgressBar, "ui_main_QProgressBar_status")
        if progress_value:
            qb.setValue(int(progress_value))
            qb.setFormat(f"{qb.value()}%")
        else:
            qb.setValue(0)
            qb.setFormat("")

    # 倒计时的方式，保留
    # self.i = 3
    # self.timer.start(1000)  # 设置定时器的定时间隔时间，为1000ms，即1秒
    # self.timer.timeout.connect(self.timeStart)  # 将timeout信号与槽timeStart连接,每隔一秒钟调用一次timeStart函数

    # def timeStart(self):
    #     # 将数值减1，并在lcd控件中显示
    #     qb = self.findChild(QProgressBar, "view1_6_progress_bar")
    #     # print(f"进度:{self.i}%")
    #     qb.setFormat(f"进度:{self.i}%")
    #     self.i -= 1
    #     if self.i == 0:
    #         qb.setValue(0)
    #         self.timer.stop()

    @staticmethod
    def font_ui(family="微软雅黑", size=10, bold=False, italic=False,
                underline=False, strike_out=False, kerning=False) -> QFont:
        """默认 ui 字体
        Args:
            family (str): 字体，默认"微软雅黑"
            size (int): 字号：默认 10
            bold (bool):加粗：默认 False
            italic (bool):斜体：默认 False
            underline (bool):下划线：默认 False
            strike_out (bool):删除线：默认 False
            kerning (bool):是否紧凑：默认 False

        Returns:QFont 默认字体
        """
        font_default = QFont()
        font_default.setFamily(family)  # 字体
        font_default.setPointSize(size)  # 大小
        font_default.setBold(bold)  # 加粗
        font_default.setItalic(italic)  # 斜体
        font_default.setUnderline(underline)  # 下划线
        font_default.setWeight(QFont.Normal)  # 字重
        font_default.setStrikeOut(strike_out)  # 删除线
        font_default.setKerning(kerning)  # 字间距紧凑

        return font_default

    @staticmethod
    def set_text_color(obj: object) -> None:
        """设置对象文字颜色

        :param obj:对象, 可以是 QComboBox, QLineEdit
        :type obj:
        :return:None
        :rtype:None
        """
        if type(obj) == QComboBox:
            if obj.currentText():
                obj.setStyleSheet("QComboBox{ color: rgba(30,48,86,255); }")
            else:
                obj.setStyleSheet("QComboBox{ color: rgba(30,48,86,99); }")
        elif type(obj) == QLineEdit:
            if obj.text():
                obj.setStyleSheet("QLineEdit{ color: rgba(30,48,86,255); }")
            else:
                obj.setStyleSheet("QLineEdit{ color: rgba(30,48,86,99); }")

    @staticmethod
    def color_text(r: int = 30, g: int = 47, b: int = 86, a: int = 99) -> QPalette:
        """字体调色板
        Args:
            r (int): 红->默认 30
            g (int): 绿->默认 47
            b (int): 蓝->默认 86
            a (int): 透明度，默认 99

        Returns:QPalette
        """
        palette = QPalette()
        palette.setColor(QPalette.ColorRole.Text, QColor(r, g, b, a))
        palette.setColor(QPalette.ColorRole.PlaceholderText, QColor(r, g, b, a))
        return palette

    @staticmethod
    def set_table_column_width_resize_to_contents_interactive(table: object, column: int) -> None:
        """设置表格的列宽按照内容宽度且可以变动

        Args:
            table (QTableWidget): 目标表格
            column (int):列索引，索引从 0 开始

        Returns:None

        """
        table.horizontalHeader().setSectionResizeMode(column, QHeaderView.ResizeMode.ResizeToContents)  # 标题自适应宽度
        width = table.columnWidth(column)
        table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        width = max(120, width) + 16  # 加 16 保证 标题都能显示出来

        rows = table.rowCount()
        # 当前列每行内容中是否有超过 200 字符
        if any(len(table.item(row, column).text()) > 20 for row in range(rows)):
            table.setColumnWidth(column, 300)
            # print("300")
        else:
            table.setColumnWidth(column, width)