# -*- encoding: utf-8 -*-
"""
@File           :   threads_jpz.py
@Time           :   2022-11-10, 周四, 20:44
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   线程池
"""

import copy
import os
from random import random

from PySide6.QtCore import QThread, Qt, Signal
from PySide6.QtWidgets import QTableWidgetItem

from configfiles import (APP_DATA_PBI_UTILS, COLUMN_NAME, DATA_CATEGORY, PERMISSION_EDIT_COLUMN_NAME,
                         PERMISSION_EDIT_COLUMN_NAME_ROLE, REPORT_PAGE_BASE_JSON, REPORT_VISUAL_COLUMN_NAME)
from ui.method import UiMethod
from utils.methods import create_folder, distinct_list_text, read_json, write_json_in_file
from utils.pbit import Pbit


def custom_json_rewrite(json_path: str, json_dic: dict, folder_: str = "custom") -> None:
    """自定义文件夹下 json 文件写入

    Args:
        json_path (str): json 文件名称，需要带拓展名
        json_dic (dict): 写入的 json dict
        folder_ (str): 自定义文件夹，默认是为 custom
    """
    folder_path = os.path.join(APP_DATA_PBI_UTILS, folder_)
    create_folder(folder_path)
    json_path = os.path.join(folder_path, json_path)
    write_json_in_file(json_path, json_dic)


def table_load_json_permission_list(table, dic_list):
    """加载 config 中权限表格数据
    
    :param table: 目标表格
    :type table: QTableWidget
    :param dic_list: 字典列表
    :type dic_list: list
    :return: None
    :rtype: None
    """
    row_count = len(dic_list)
    table.setRowCount(row_count)
    # 循环写入
    for row, dic in enumerate(dic_list):
        for col, name in enumerate(dic):
            item = ",".join(dic[name]) if type(dic[name]) == list else str(dic[name])  # 列表转换成字符串
            cell = QTableWidgetItem(item)
            cell.setTextAlignment(4)  # 对齐方式 0:left 1:left 2:right 3:right 4:centre
            # cell.setFlags(Qt.ItemFlag.ItemIsEnabled)
            table.setItem(row, col, cell)


def table_load_json_report_visual_templates(table, dic_list):
    """加载 config 中模板度量值表格数据 ui_02

    :param table: 目标表格
    :type table: QTableWidget
    :param dic_list: 字典列表
    :type dic_list: list
    :return: None
    :rtype: None
    """
    row_count = len(dic_list)
    table.setRowCount(row_count)
    # 循环写入
    for row, dic in enumerate(dic_list):
        for col, name in enumerate(dic):
            item = dic[name]
            if col == 2:
                item = DATA_CATEGORY[dic[name]]  # 度量值类别

            cell = QTableWidgetItem(item)
            cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
            if col == 3:
                if dic[name]:
                    # Unchecked = ...  # type: Qt.CheckState
                    # PartiallyChecked = ...  # type: Qt.CheckState
                    # Checked = ...  # type: Qt.CheckState
                    cell.setCheckState(Qt.CheckState.Checked)
                else:
                    cell.setCheckState(Qt.CheckState.Unchecked)
            table.setItem(row, col, cell)


def table_write_json_permission_list(dic_list, user_list, path, dic_json):
    """写入权限表

    :param dic_list: 字典列表
    :type dic_list: list
    :param user_list: 用户列表
    :type user_list: list
    :param path: json 文件路径
    :type path: str
    :param dic_json: json 文件的字典
    :type dic_json: dict
    :return: None
    :rtype: None
    """

    dic_json["PermissionList"] = dic_list
    dic_json["PowerBIUsers"] = user_list
    # 覆盖写入
    write_json_in_file(path, dic_json)


class ThreadExtractPbix(QThread):
    """提取 pbix"""

    # signal_tuple = Signal(tuple)  # 元组中三元素，依次是 线程序号，进度数值，提取的文件夹
    signal_dic = Signal(dict)  # 元组中三元素，依次是 线程序号，进度数值，提取的文件夹,错误及错误内容

    def __init__(self,
                 path_pbix_source=None,
                 folder_pbix_extract_x=None,
                 *args, **kwargs):

        self.path_pbix_source = path_pbix_source
        self.folder_pbix_extract_x = folder_pbix_extract_x
        super().__init__(*args, **kwargs)

    def run(self):
        """执行"""

        global folder
        try:
            pbit = Pbit()
            if self.path_pbix_source is not None:
                pbit.path_pbix_source = self.path_pbix_source

            # 3、提取出 demo 文件夹中的 pbix 文件到文件夹 template/pbix_extract
            pbit.pbi_tools_command_extract(self.folder_pbix_extract_x)
            folder = pbit.folder_pbix_extract(self.folder_pbix_extract_x)
            if not pbit.folder_pbix_extract_status:
                pbit.delete_folder_pbix_extract(self.folder_pbix_extract_x)
                # 元组中五元素，依次是 线程序号，进度数值，提取的文件夹,状态，错误及错误内容
                self.signal_dic.emit({"id"    : self.folder_pbix_extract_x, "progress": 0,
                                      "folder": folder, "status": False, "error": "extract_error"})
                return

            self.signal_dic.emit({"id": self.folder_pbix_extract_x, "progress": 100, "folder": folder})
        except PermissionError:
            self.signal_dic.emit({"id": self.folder_pbix_extract_x, "progress": 0, "status": False, "error": "PermissionError"})
        except Exception:
            self.signal_dic.emit({"id": self.folder_pbix_extract_x, "progress": 0, "folder": folder})


class ThreadCreateJson(QThread, UiMethod):
    """创建 json 文件
    """
    # progress = Signal(int)  # 进度
    signal_dic = Signal(dict)  # {"progress": 0, "status": True}

    def __init__(self,
                 parent=None,
                 path_pbix_source=None,
                 new_report_pages_list=None,
                 config_json_path=None,
                 page_name_is_uuid=False,
                 measure_is_hidden=False,
                 *args, **kwargs):
        self.parent = parent
        self.path_pbix_source = path_pbix_source
        self.new_report_pages_list = new_report_pages_list
        self.config_json_path = config_json_path
        self.page_name_is_uuid = page_name_is_uuid
        self.measure_is_hidden = measure_is_hidden
        super().__init__(*args, **kwargs)

    def run(self):
        """执行
        """
        try:
            pbit = Pbit()
            if self.path_pbix_source is not None:
                pbit.path_pbix_source = self.path_pbix_source

            pbit.page_name_is_uuid = self.page_name_is_uuid
            # 3、提取出 demo 文件夹中的 pbix 文件到文件夹 template/pbix_extract
            pbit.pbi_tools_command_extract()
            if not pbit.folder_pbix_extract_status:
                pbit.delete_folder_pbix_extract()
                self.signal_dic.emit({"progress": 0, "status": False, "error": "extract_error"})
                return

            self.signal_dic.emit({"progress": int(random() * 80), "status": True})

            str0 = self.new_report_pages_list
            if len(str0) > 0:
                if str0[-1] == ",":
                    str0 = str0[:-1]
                str_list = str0.split(',')  # 字符串拆成列表
                int_list = [int(_) for _ in str_list]  # 字符串转整数
                pbit.new_report_pages_list = int_list

            if self.config_json_path is not None:
                pbit.config_json_path = self.config_json_path
            # 5、初始化页面配置文件 ./demo/source/config/ReportPages.json,便于后续使用
            pbit.init_config_json(self.measure_is_hidden)
            # 删除提取的文件
            pbit.delete_folder_pbix_extract()

            self.signal_dic.emit({"progress": 100, "status": True})

        except PermissionError:
            self.signal_dic.emit({"progress": 0, "status": False, "error": "PermissionError"})
        except Exception:
            self.signal_dic.emit({"progress": 0, "status": False})


class ThreadCompile(QThread):
    """编译
    """
    signal_dic = Signal(dict)  # {"progress": 0, "status": True}

    def __init__(self,
                 path_pbix_source=None,
                 config_json_path=None,
                 # path_pbit_target=None,
                 new_report_pages_list=None,
                 name_measure_table=None,
                 name_dax_folder_parent=None,

                 *args, **kwargs):
        self.path_pbix_source = path_pbix_source
        self.config_json_path = config_json_path
        # self.path_pbit_target = path_pbit_target
        self.new_report_pages_list = new_report_pages_list
        self.name_measure_table = name_measure_table
        self.name_dax_folder_parent = name_dax_folder_parent

        super().__init__(*args, **kwargs)

    def run(self):
        """执行生成 pbit
        """
        try:
            pbit = Pbit()
            if self.path_pbix_source is not None:
                pbit.path_pbix_source = self.path_pbix_source

            if self.config_json_path is not None:
                pbit.config_json_path = self.config_json_path

            if self.new_report_pages_list is not None:
                pbit.new_report_pages_list = self.new_report_pages_list

            if self.name_measure_table is not None:
                pbit.name_measure_table = self.name_measure_table

            if self.name_dax_folder_parent is not None:
                pbit.name_dax_folder_parent = self.name_dax_folder_parent

            # 3、提取出 demo 文件夹中的 pbix 文件到文件夹 template/pbix_extract
            pbit.pbi_tools_command_extract()

            # 判断是否提取正确的文件
            if not pbit.folder_pbix_extract_status:
                pbit.delete_folder_pbix_extract()
                self.signal_dic.emit({"progress": 0, "status": False, "error": "extract_error"})
                return

            # 判断提取模型是否与 config.json 匹配
            if not pbit.extract_is_match_config_json():
                pbit.delete_folder_pbix_extract()
                self.signal_dic.emit({"progress": 0, "status": False, "error": "not_match"})
                return
            # 创建项目文件夹
            pbit.create_folder_project()
            i = 10
            self.signal_dic.emit({"progress": i, "status": True})
            # 4、初始化临时文件夹
            pbit.init_folder_temp()
            # 6、根据上述的配置文件,写入导航度量值
            pbit.rewrite_dax_from_template_dax_list()
            # 写入视觉度量值
            pbit.write_report_visual_template_measures()
            # 写入rls度量值
            pbit.write_rls_measures()
            # 写入rls database.json
            pbit.rewrite_database_json_rls()
            # 复制模板中 M 查询到模型中
            pbit.create_queries_in_model_queries()
            # 复制模板中表格到模型中
            pbit.create_table_in_model_tables()
            # 重写database.json 中的 queryGroups 结点
            pbit.rewrite_database_json_queryGroups()
            # 重写database.json annotations 结点
            pbit.rewrite_database_json_annotations()
            i += random() * 10
            self.signal_dic.emit({"progress": i, "status": True})
            # 7、生成内容页及无权限页
            pbit.generate_report_page(["PageTitle"])
            i += random() * 10
            self.signal_dic.emit({"progress": i, "status": True})
            # 8、重写 temp->Report->report.json
            pbit.rewrite_report_json()
            # 9、重写 temp/Report/sections/xxx_xxx/section.json
            pbit.rewrite_page_section_json()
            # 10、重写 temp/Report/sections/xxx_xxx/config.json
            pbit.rewrite_page_config_json()
            i += random() * 10
            self.signal_dic.emit({"progress": i, "status": True})
            # 12、复制按钮模板到对应导航页面待用
            pbit.copy_navigation_button()
            # 13、重写导航按钮的 json 文件,绑定度量值
            pbit.rewrite_navigation_button_json()
            i += random() * 40
            self.signal_dic.emit({"progress": i, "status": True})
            # 度量值更换表后,修改每个页面下视觉对象度量值表名称
            pbit.rewrite_all_page_visualcontainers_visual_config_json_measure_table_change("Measure", pbit.name_measure_table)
            # 14、编译修改后的 pbix 提取出的文件到 demo 文件夹
            pbit.pbi_tools_command_compile()
            # 删除提取的文件
            pbit.delete_folder_pbix_extract()
            # pbit 文件未生成
            if not pbit.pbi_tools_command_compile_status:
                self.signal_dic.emit({"progress": 0, "status": False, "error": "compile_error"})
                return

            self.signal_dic.emit({"progress": 100, "status": True})

        except PermissionError:
            self.signal_dic.emit({"progress": 0, "status": False, "error": "PermissionError"})

        except Exception:
            self.signal_dic.emit({"progress": 100, "status": False})


class ThreadLoadJsonReportVisualEdit(QThread, UiMethod):
    """加载 ui_02

    成功/失败
    成功：1
    失败：0
    """

    status = Signal(int)

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """执行加载 ReportVisualTemplates
        """
        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            dic_list = dic_json["ReportVisualTemplates"]
            table_load_json_report_visual_templates(self.table, dic_list)
            self.status.emit(1)
        except Exception:
            self.table.setRowCount(0)
            self.status.emit(0)


class ThreadSaveJsonReportVisualEdit(QThread, UiMethod):
    """保存视觉对象的度量值的配置文件


    # 成功/失败
    # 成功：1
    # 失败：0
    """
    status = Signal(dict)  # {"status":0,"row":1,"col":2,"error": "blank"}

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """保存 ReportVisualTemplates
        """

        try:  # 保证是正确的json
            table = self.table
            row = table.rowCount()
            col = table.columnCount()

            if row and col:
                keys = list(DATA_CATEGORY.keys())
                values = list(DATA_CATEGORY.values())
                ReportVisualTemplates = []
                name_list = []
                keys_col = list(REPORT_VISUAL_COLUMN_NAME.keys())
                for r in range(row):
                    dic_col_copy = copy.deepcopy(REPORT_VISUAL_COLUMN_NAME)
                    for c, name in enumerate(dic_col_copy):
                        cell_text = table.item(r, c).text()
                        col_display = REPORT_VISUAL_COLUMN_NAME[keys_col[c]]  # 字段显示文本
                        if c < 3 and cell_text == "":  # 数据不为空
                            self.status.emit({"status": 0, "row": r + 1, "col": f"{c + 1}({col_display})", "error": "blank"})
                            return
                        if c == 0 and cell_text in name_list:  # 数据不重复
                            row_old = name_list.index(cell_text)
                            self.status.emit({"status": 0, "row_old": row_old + 1, "row": r + 1, "error": "repeat", "col": f"{c + 1}({col_display})"})
                            return
                        elif c == 0:
                            name_list.append(cell_text)

                        # 赋值
                        if c == 2:
                            dic_col_copy[name] = keys[values.index(cell_text)]
                        elif c == 3:
                            dic_col_copy[name] = table.item(r, c).checkState() == Qt.CheckState.Checked
                        else:
                            dic_col_copy[name] = cell_text
                    ReportVisualTemplates.append(dic_col_copy)
                # 读取基础报表配置
                dic_json = read_json(self.path)
                dic_json["ReportVisualTemplates"] = ReportVisualTemplates
                # 覆盖写入
                write_json_in_file(self.path, dic_json)  # 覆盖写入
                # 覆盖写入自定义模板
                custom_json_rewrite("report_visual_templates_base.json", {"ReportVisualTemplates": ReportVisualTemplates})
                self.status.emit({"status": 1})

        except PermissionError:
            self.status.emit({"progress": 0, "status": False, "error": "PermissionError"})
        except Exception:
            self.status.emit({"status": 0})


class ThreadSaveJsonPermissionList(QThread, UiMethod):
    """保存权限表
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(int)

    def __init__(self, rls_name, table_name, column_name, column_value, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.rls_name = rls_name
        self.table_name = table_name
        self.column_name = column_name
        self.column_value = column_value
        self.path = path
        self.table = table

    def run(self):
        """保存 PermissionList
        """

        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            dic_list = dic_json["PermissionList"]
            user_list = dic_json["PowerBIUsers"]

            tf1 = any(dic["name"] == self.rls_name for dic in dic_list)
            tf2 = any(dic["tableColumn"] == f"'{self.table_name}'[{self.column_name}]" for dic in dic_list)

            if tf1 or tf2:
                self._edit_json(dic_list, user_list, dic_json)
            else:
                self._add_new_json(dic_list, user_list, dic_json)

        except Exception:
            self.status.emit(0)

    def _add_new_json(self, dic_list, user_list, dic_json):
        """新增行

        :param dic_list: 字典列表
        :type dic_list: list
        :param user_list: 用户列表
        :type user_list: list
        :param dic_json: json 文件的字典
        :type dic_json: dict
        :return: None
        :rtype: None
        """
        cell_c1_value = f"'{self.table_name}'[{self.column_name}]"
        cell_c2_value = distinct_list_text(self.column_value, "\n")

        dic = {"name": self.rls_name, "tableColumn": cell_c1_value, "value": cell_c2_value}
        dic_list.append(dic)

        if user_list:
            self._add_new_PowerBIUsers_roles(cell_c1_value, cell_c2_value, user_list)
        table_load_json_permission_list(self.table, dic_list)
        table_write_json_permission_list(dic_list, user_list, self.path, dic_json)
        self.status.emit(1)

    def _add_new_PowerBIUsers_roles(self, cell_c1_value, cell_c2_value, user_list):
        """新增 PowerBIUsers roles

        :param cell_c1_value: 维度
        :type cell_c1_value: str
        :param cell_c2_value: 维度值
        :type cell_c2_value: list
        :param user_list: 用户列表
        :type user_list: list
        :return: None
        :rtype: None
        """
        role_copy = copy.deepcopy(PERMISSION_EDIT_COLUMN_NAME_ROLE)
        keys = list(PERMISSION_EDIT_COLUMN_NAME_ROLE)
        role_copy[keys[0]] = self.rls_name
        role_copy[keys[1]] = cell_c1_value
        role_copy[keys[2]] = cell_c2_value
        for user in user_list:
            user["roles"].append(role_copy)

    def _edit_PowerBIUsers_roles(self, cell_c2_value, user_list):
        """编辑 PowerBIUsers roles

        :param cell_c2_value: 维度值
        :type cell_c2_value: list
        :param user_list: 用户列表
        :type user_list: list
        :return: None
        :rtype: None
        """
        for user in user_list:
            for role in user["roles"]:
                if role["permissionName"] == self.rls_name:
                    role["value"] = cell_c2_value

    def _edit_json(self, dic_list, user_list, dic_json):
        """编辑 json

        :param dic_list: 字典列表
        :type dic_list: list
        :param user_list: 用户列表
        :type user_list: list
        :param dic_json: json 文件的字典
        :type dic_json: dict
        :return: None
        :rtype: None
        """

        cell_c1_value = f"'{self.table_name}'[{self.column_name}]"
        cell_c2_value = distinct_list_text(self.column_value, "\n")

        for dic in dic_list:
            if dic["name"] == self.rls_name:
                dic["tableColumn"] = cell_c1_value
                dic["value"] = cell_c2_value
                break
        dic_json["PermissionList"] = dic_list

        if user_list:
            self._edit_PowerBIUsers_roles(cell_c2_value, user_list)

        table_load_json_permission_list(self.table, dic_list)
        table_write_json_permission_list(dic_list, user_list, self.path, dic_json)
        self.status.emit(1)


class ThreadDeleteJsonPermissionList(QThread, UiMethod):
    """删除权限表
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(int)

    def __init__(self, rls_name, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.rls_name = rls_name
        self.path = path
        self.table = table

    def run(self):
        """删除 PermissionList 及 PowerBIUsers 中的对应
        """

        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            dic_list = dic_json["PermissionList"]
            user_list = dic_json["PowerBIUsers"]
            tf = False
            for dic in dic_list:
                if dic["name"] == self.rls_name:
                    dic_list.remove(dic)
                    tf = True
                    break

            if tf:
                for user in user_list:
                    for role in user["roles"]:
                        if role["permissionName"] == self.rls_name:
                            user["roles"].remove(role)
                            break
                table_load_json_permission_list(self.table, dic_list)
                table_write_json_permission_list(dic_list, user_list, self.path, dic_json)
                self.status.emit(1)
            else:
                self.status.emit(0)
        except Exception:
            self.status.emit(0)


class ThreadLoadJsonPermissionList(QThread, UiMethod):
    """加载 权限表
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(dict)  # {"status": 1, "file": self.path}

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """加载 PermissionList
        """

        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            dic_list = dic_json["PermissionList"]
            table_load_json_permission_list(self.table, dic_list)
            self.status.emit({"status": 1, "file": self.path})
        except Exception:
            self.status.emit({"status": 0, "file": self.path})


class ThreadLoadJsonPermissionEdit(QThread, UiMethod):
    """加载 PermissionEdit
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(int)

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """加载 PowerBIUsers
        """

        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            user_list = dic_json["PowerBIUsers"]
            user_names = self.get_permission_edit_column_name(self.path)

            row_count = len(user_list)
            self.table.setRowCount(row_count)
            col_count = len(user_names)
            self.table.setColumnCount(col_count)

            # 加载标题
            for id, name in enumerate(user_names):
                item = QTableWidgetItem()
                item.setText(user_names[name])
                self.table.setHorizontalHeaderItem(id, item)

            # 加载数据
            not_roles_index_max = 3
            # 循环写入
            for row, dic in enumerate(user_list):
                roles = [role["value"] for role in dic["roles"]]
                for col, name in enumerate(user_names):
                    if col < not_roles_index_max:
                        item = ",".join([str(item) for item in dic[name]]) if type(dic[name]) == list else str(dic[name])  # 列表转换成字符串
                    else:
                        item = ",".join([str(item) for item in roles[col - not_roles_index_max]]) \
                            if type(roles[col - not_roles_index_max]) == list else str(roles[col - not_roles_index_max])  # 列表转换成字符串

                    cell = QTableWidgetItem(item)
                    cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
                    # cell.setFlags(Qt.ItemFlag.ItemIsEnabled)
                    self.table.setItem(row, col, cell)
            self.status.emit(1)
        except Exception:
            self.status.emit(0)


class ThreadAddJsonPermissionEdit(QThread, UiMethod):
    """新增行 PermissionEdit
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(int)

    def __init__(self, path, table, row_list=None, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table
        self.row_list = row_list

    def run(self):
        """新增行 PermissionEdit
        """

        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            dic_page = dic_json["ReportPages"]
            dic_perm = dic_json["PermissionList"]

            page = [str(dic["ordinal"]) for dic in dic_page]
            page = ",".join(page)
            rls_value = [",".join(dic["value"]) for dic in dic_perm]
            rls_value.insert(0, page)

            col_name_new = self.get_permission_edit_column_name(self.path)

            if self.row_list is None:
                self.table.insertRow(0)
                self.row_list = [0]
                for row in self.row_list:
                    for index, _ in enumerate(col_name_new):
                        if index <= 2:
                            cell = QTableWidgetItem()
                            cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
                            self.table.setItem(row, index, cell)

            for row in self.row_list:
                for index, _ in enumerate(col_name_new):
                    if index > 2:
                        content = rls_value[index - 3]
                        cell = QTableWidgetItem(content)
                        cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
                        self.table.setItem(row, index, cell)

            self.status.emit(1)
        except Exception:
            self.status.emit(0)


class ThreadSaveJsonPermissionEdit(QThread, UiMethod):
    """保存 PermissionEdit
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(dict)  # {"status": 0, "row": row + 1, "col": col + 1, "error": "blank"}

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """保存修改后的 PowerBIUsers
        """

        try:
            table = self.table
            row = table.rowCount()
            col = table.columnCount()
            dic_col = self.get_permission_edit_column_name(self.path)

            keys = list(PERMISSION_EDIT_COLUMN_NAME_ROLE)
            keys_display = list(PERMISSION_EDIT_COLUMN_NAME)
            power_bi_users = []
            if row and col:
                for r in range(row):
                    roles = []
                    dic_col_copy = copy.deepcopy(dic_col)
                    dic_copy = copy.deepcopy(PERMISSION_EDIT_COLUMN_NAME)
                    role_copy = copy.deepcopy(PERMISSION_EDIT_COLUMN_NAME_ROLE)
                    for c, name in enumerate(dic_col_copy):
                        # 保存前校验
                        cell_text = table.item(r, c).text()
                        if c < col and cell_text == "":  # 数据不为空
                            col_display = PERMISSION_EDIT_COLUMN_NAME[keys_display[c]]  # 字段显示文本
                            self.status.emit({"status": 0, "row": r + 1, "col": f"{c + 1}({col_display})", "error": "blank"})
                            return

                        if c < 2:
                            dic_copy[name] = cell_text
                        elif c == 2:
                            dic_copy[name] = distinct_list_text(cell_text, ",")
                        else:
                            role_copy[keys[0]] = name
                            role_copy[keys[1]] = dic_col_copy[name]
                            role_copy[keys[2]] = distinct_list_text(cell_text, ",")
                            if c == 3:
                                role_copy[keys[2]] = distinct_list_text(cell_text, ",", is_int=True)
                            roles.append(copy.deepcopy(role_copy))
                    dic_copy["roles"] = roles
                    power_bi_users.append(copy.deepcopy(dic_copy))

            # 读取基础报表配置
            dic_json = read_json(self.path)
            dic_json["PowerBIUsers"] = power_bi_users
            # 覆盖写入
            write_json_in_file(self.path, dic_json)
            self.status.emit({"status": 1})
        except Exception:
            self.status.emit({"status": 0})


class ThreadLoadJsonReportPage(QThread, UiMethod):
    """加载 ReportPage
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(int)

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """加载 report page
        """

        try:  # 保证是正确的json
            dic_json = read_json(self.path)
            dic_list = dic_json["ReportPages"]
            row_count = len(dic_list)
            self.table.setRowCount(row_count)
            # 循环写入
            for row, dic in enumerate(dic_list):
                for col, name in enumerate(dic):
                    cell = QTableWidgetItem(str(dic[name]))
                    cell.setTextAlignment(4)  # 0:left 1:left 2:right 3:right 4:centre
                    if col == 0:  # 不能编辑
                        cell.setFlags(Qt.ItemFlag.ItemIsEnabled)
                    self.table.setItem(row, col, cell)
            self.status.emit(1)
        except Exception:
            self.status.emit(0)


class ThreadSaveJsonReportPage(QThread):
    """保存 ReportPage
    """
    # 成功/失败
    # 成功：1
    # 失败：0
    status = Signal(dict)  # {"status": 0, "row": row + 1, "col": col + 1, "error": "blank"}

    def __init__(self, path, table, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.path = path
        self.table = table

    def run(self):
        """保存修改后的 report page
        """

        try:
            table = self.table
            rows = table.rowCount()
            cols = table.columnCount()
            report_pages = []
            if rows and cols:
                dic = copy.deepcopy(COLUMN_NAME)
                keys = list(COLUMN_NAME.keys())
                int_keys = ["ordinal", "displayOption", "height", "width", "visibility"]  # 整数类型的字段
                not_repeat_col_list = [0, 1, 11]  # 每列数据不能重复的列号列表
                value_lists = [[] for _ in not_repeat_col_list]
                for row in range(rows):
                    for col, name in enumerate(dic):
                        cell_text = table.item(row, col).text()
                        col_display = COLUMN_NAME[keys[col]]  # 字段显示文本
                        # 保存前校验
                        if col < cols - 1 and cell_text == "":  # 数据不为空
                            self.status.emit({"status": 0, "row": row + 1, "col": f"{col + 1}({col_display})", "error": "blank"})
                            return

                        if col in not_repeat_col_list:
                            index = not_repeat_col_list.index(col)
                            if cell_text in value_lists[index]:  # 数据不重复
                                self.status.emit({"status": 0, "row_old": row, "row": row + 1, "error": "repeat", "col": f"{col + 1}({col_display})"})
                                return
                            else:
                                value_lists[index].append(cell_text)
                        # 赋值
                        dic[name] = int(cell_text) if name in int_keys else cell_text
                    report_pages.append(dic.copy())

                # 读取基础报表配置
                dic_json = read_json(self.path)

                dic_json["ReportPages"] = report_pages
                write_json_in_file(self.path, dic_json)  # 覆盖写入
                REPORT_PAGE_BASE_JSON["ReportPages"] = report_pages[:3]
                custom_json_rewrite("report_base.json", REPORT_PAGE_BASE_JSON)  # 覆盖写入自定义报表配置
                self.status.emit({"status": 1})
        except PermissionError:
            self.status.emit({"progress": 0, "status": False, "error": "PermissionError"})
        except Exception:
            self.status.emit({"status": 0})