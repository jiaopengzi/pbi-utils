# -*- encoding: utf-8 -*-
"""
@File           :   pbit.py
@Time           :   2022-11-10, 周四, 19:32
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   pbit 类 主要文件
"""

import copy
import json
import os
import re
import shutil
import time
import uuid
from string import Template

from config import BASE_DIR, runtime_tmpdir, set_custom_report_base, set_custom_report_visual_templates_base
from configfiles import DAX_REPORT_VISUAL_TEMPLATE, REPORT_PAGE_BASE_JSON, REPORT_VISUAL_TEMPLATES_BASE, RLS_DAX_TEMPLATE, TEMPLATE_DAX_LIST
from utils.methods import (create_folder, execCmd, init_folder, read_file_to_str, read_json, write_json_in_file, write_str_in_file)


class Pbit(object):
    """
    # 1、利用 pbi-tools 工具,提取出 PBIX 模板中相应信息。
    # 2、外部解耦 PBIX 文件的模板配置 json 文件。
    # 3、根据模板 + 解耦 json 文件, 快速生成配置的 PBIT。
    """

    # 一、类属性
    # python 文件路径, 当前的方法保证 pyinstaller 打包单个 exe 运行路径是正确的。
    exe_pbi_tools = os.path.join(runtime_tmpdir(), "pbi_tools\\pbi-tools.exe")  # pbi-tools.exe 工具路径
    pbix_source = "165.pbix"  # demo 默认 PBIX 源路径
    folder_demo = os.path.join(BASE_DIR, "demo")  # demo 文件夹
    path_pbix_source = os.path.join(folder_demo, pbix_source)  # 选择的 pbix 路径
    path_pbit_target = os.path.join(folder_demo, "demo1.40.pbit")  # 编译后 PBIT 路径,按照时间给定版本
    folder_template = os.path.join(runtime_tmpdir(), "template")  # 模板文件夹
    folder_project = None  # 生成的项目文件夹

    def content_page(self, folder_pbix_extract_x: int = None) -> str:
        """获取内容页的在提取后的文件夹中的文件夹名称.

        Args:
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:
            内容页的文件夹名称.

        """
        content_page = "001_Navigation"
        content_prefix = "Report\\sections\\"
        content_path = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), content_prefix + content_page)

        if os.path.exists(content_path):  # 如果有 content_page = "001_Navigation" 存在就直接返回。
            return content_page

        # 根据页面的序号为 1 来判定内容页.
        dic = read_json(self.config_json_path)
        report_pages = dic["ReportPages"]
        for page in report_pages:
            if page["ordinal"] == 1:
                return f'{str(page["ordinal"]).zfill(3)}_{page["displayName"]}'

    folder_dax = os.path.join(folder_template, "dax")  # DAX 模板 template->dax
    folder_navigation_button = os.path.join(folder_template, "navigation_button")  # 导航按钮模板 template->navigation_button
    folder_config = os.path.join(folder_demo, "source\\config")  # config 配置文件夹路径
    config_json_path = os.path.join(folder_config, "config.json")  # 自定义的配置 json 文件路径
    folder_temp = os.path.join(os.getenv("TEMP"), "pbit_temp")  # temp 临时文件夹,获取用户的临时文件夹 os.getenv("TEMP")
    database_json_path = os.path.join(folder_temp, "Model\\database.json")  # Model\database.json

    folder_pbix_extract_status = True  # 提取状态是否正确，默认 True

    def folder_pbix_extract(self, folder_pbix_extract_x: int = None, create=False) -> str:
        """提取出来的模板

        默认是os.getenv("TEMP")->pbix_extract
        :param folder_pbix_extract_x: 第几个提取的文件, 默认None
        :type folder_pbix_extract_x: int
        :param create: 是否是创建提取文件夹
        :type create:bool
        :return: 返回文件夹路径
        :rtype: str
        """

        folder0 = os.path.join(os.getenv("TEMP"), "pbix_extract")
        folder1 = os.path.join(os.getenv("TEMP"), f"pbix_extract_{folder_pbix_extract_x}")
        if create:
            self.folder_pbix_extract_status = True
            return folder1 if folder_pbix_extract_x else folder0
        if folder_pbix_extract_x and os.path.getsize(folder1):  # 不为空
            self.folder_pbix_extract_status = True
            return folder1
        elif not folder_pbix_extract_x and os.path.getsize(folder0):  # 不为空
            self.folder_pbix_extract_status = True
            return folder0
        elif folder_pbix_extract_x and not os.path.getsize(folder1):  # 为空
            self.folder_pbix_extract_status = False
            return str(self.path_pbix_source[:-5])
        elif not folder_pbix_extract_x and not os.path.getsize(folder0):  # 为空
            self.folder_pbix_extract_status = False
            return str(self.path_pbix_source[:-5])

        return folder0

    name_measure_table = "Measure"  # 度量值文件夹
    name_dax_folder_parent = "99_config"  # config 度量值的顶层文件夹

    def set_measures_folder(self, path: str = None, folder: str = None) -> str:
        """设置度量值所在文件夹，如果不存在就创建。

        Args:
            path (str): 需要设置的文件夹路径
            folder (str): 具体的度量值文件夹

        Returns:
            返回设置好地度量值文件夹路径
        """
        if path is None and folder is None:
            return os.path.join(self.folder_temp, "Model\\tables\\" + self.name_measure_table + "\\measures")
        elif path is None and folder:
            return os.path.join(self.folder_temp, "Model\\tables\\" + folder + "\\measures")
        elif path and folder:
            return os.path.join(path, "Model\\tables\\" + folder + "\\measures")

    # Report->report.json
    path_report_json = os.path.join(folder_temp, "Report\\report.json")

    # temp->Report->sections
    folder_sections = os.path.join(folder_temp, "Report\\sections")

    # 报表也分类数量,默认值为1
    new_report_pages_list = [7]

    # 是否随机伪加密报表 name,模式要加密
    page_name_is_uuid = True

    # 二、通用函数

    def get_report_name_id(self, key):
        """为了实现报表页面权限的伪加密,名称 随机值 uuid 统一函数。可以使用内置 uuid 模块的 uuid4

        :param key: 关键字
        :type key:str
        :return:uuid后的结果
        :rtype:str
        """

        return str(uuid.uuid4()) if self.page_name_is_uuid else key

    @staticmethod
    def get_button_position_dic(page_count, index=0, col_number=8,
                                width=140, height=56,
                                page_width=1280, page_height=792,
                                left=16, top=80, bottom=40):
        """按钮位置函数 get_button_position_dic

        返回第一个(ordinal 从 0 开始)按钮的水平和垂直位置。
        需要导航总页数:page_count
        矩形按钮宽高:width=168, height=64
        页面宽度:1280 - 16 * 2 = 1248 page_width
        页高度 page_height = 792
        左边距 left = 16
        上边距top = 80
        下边距 bottom = 40
        暂定一排最多 6 个按钮 col_number = 6
        get_button_position_dic(20,0)
        :param page_count:需要导航总页数
        :type page_count:int
        :param index:按钮的索引第几个
        :type index:int
        :param col_number:列数
        :type col_number:int
        :param width:矩形按钮宽
        :type width:int
        :param height:矩形按钮高
        :type height:int
        :param page_width:页面宽度
        :type page_width:int
        :param page_height:页面高度
        :type page_height:int
        :param left:左边距
        :type left:int
        :param top:上边距
        :type top:int
        :param bottom:下边距
        :type bottom:int
        :return:返回值为字典, 例如：{'x': 16, 'y': 1182, 'z': 18912, 'width': 168, 'height': 64, 'tabOrder': 18912}
        :rtype:dict
        """

        content_width = page_width - left * 2  # 中间内容宽度 = 1280 - 16 * 2 = 1248
        content_height = page_height - top - bottom  # 中间内容高度 = 792 - 80 - 40 = 672
        col_spacing = int((content_width - (width * col_number)) / (col_number - 1))  # 列间隔 = (1248 - (168 * 6)) / (6 - 1) = 48
        row_number_all = page_count // col_number + 1  # 总行数 = 40 // 6 + 1 = 7
        row_spacing = int(content_height / row_number_all) - height  # 行间隔 = 672 / 7 = 40
        x = int(index % col_number * (width + col_spacing) + left)  # 按钮水平位置x
        y = int(index // col_number * (height + row_spacing) + top)  # 按钮垂直位置y
        z = x * y
        return {"x": x, "y": y, "z": z, "width": width, "height": height, "tabOrder": z}  # 返回字典

    def extract_is_template_sections_page_x(self, ordinal: int, visual_keyword: str, folder_pbix_extract_x: int = None) -> bool:
        """提取内容中 ordinal 页是否符合模板页面要求 根据页面的 关键 visual 来判断

        :param ordinal:页面序号, 从 0 开始
        :type ordinal:int
        :param visual_keyword: 视觉对象关键字
        :type visual_keyword: int
        :param folder_pbix_extract_x: 第几个提取的文件,默认是空
        :type folder_pbix_extract_x: int
        :return:符合返回 True 不符合返回 False
        :rtype:bool
        """
        dic = read_json(self.config_json_path)
        report_pages = dic["ReportPages"]
        folder_sections = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), "Report\\sections")
        page = report_pages[ordinal]["displayName"]  # 第x页名称
        folder = self.folder_visualcontainers(page, folder_sections)  # 第x页视觉对象文件夹
        if folder and os.path.exists(folder):
            is_template = any(visual.__contains__(visual_keyword) for visual in os.listdir(folder))  # 第x页视觉对象是否包含 visual_keyword
            if is_template:
                return True
        return False

    def folder_home_page(self, folder_pbix_extract_x: int = None) -> str:
        """获取 home 页的在提取后的文件夹中的文件夹具体路径

        Args:
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:
            home 页的文件夹路径
        """
        # 是否满足模板要求，满足模板要求就从提取提取文件中获取
        home = self.extract_is_template_sections_page_x(0, "Next", folder_pbix_extract_x)
        navigation = self.extract_is_template_sections_page_x(1, "Previous", folder_pbix_extract_x)
        no_permission = self.extract_is_template_sections_page_x(2, "Previous", folder_pbix_extract_x)
        if home and navigation and no_permission:
            dic = read_json(self.config_json_path)
            report_pages = dic["ReportPages"]
            content_page = report_pages[0]["displayName"]  # 第一页名称
            sections = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), "Report\\sections")
            for page in os.listdir(sections):
                if page.__contains__(content_page):
                    return os.path.join(sections, page)
        # 不满足模板要求就从 template -> sections\\001_Navigation 获取
        return os.path.join(self.folder_template, "sections\\000_Home")

    def folder_content_page(self, folder_pbix_extract_x: int = None) -> str:
        """获取内容页的在提取后的文件夹中的文件夹具体路径

        Args:
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:
            内容页的文件夹路径
        """
        # 是否满足模板要求，满足模板要求就从提取提取文件中获取
        if self.extract_is_template_sections_page_x(1, "Previous", folder_pbix_extract_x):
            dic = read_json(self.config_json_path)
            report_pages = dic["ReportPages"]
            content_page = report_pages[1]["displayName"]  # 第二页名称
            sections = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), "Report\\sections")
            for page in os.listdir(sections):
                if page.__contains__(content_page):
                    return os.path.join(sections, page)
        # 不满足模板要求就从 template -> sections\\001_Navigation 获取
        return os.path.join(self.folder_template, "sections\\001_Navigation")

    def folder_no_permission_page(self, folder_pbix_extract_x: int = None) -> str:
        """获取无权限页的在提取后的文件夹中的文件夹具体路径

        Args:
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:
            无权限页的文件夹路径
        """
        # 是否满足模板要求，满足模板要求就从提取提取文件中获取

        if self.extract_is_template_sections_page_x(2, "Previous", folder_pbix_extract_x):
            dic = read_json(self.config_json_path)
            report_pages = dic["ReportPages"]
            no_permission_page = report_pages[2]["displayName"]  # 第三页名称
            sections = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), "Report\\sections")
            for page in os.listdir(sections):
                if page.__contains__(no_permission_page):
                    return os.path.join(sections, page)
        # 不满足模板要求就从 template -> sections\\002_NoPermission 获取
        return os.path.join(self.folder_template, "sections\\002_NoPermission")

    def create_folder_project(self) -> None:
        """创建新的项目文件夹

        ├─demo_2022_XX_XX_XX_XX_XX              01、项目文件夹
        │  └─source                             02、数据和配置源文件夹
        │      ├─config                         03、配置文件夹
        │      └─data                           04、数据文件夹

        Returns:{"folder_project": config_json_path_new}
            返回新创建好的项目文件夹路径字典
        """
        time_str = time.strftime('%Y_%m_%d_%H_%M_%S', time.localtime(time.time()))  # 时间格式化字符串
        base_path = os.path.dirname(self.path_pbix_source)  # 选择的 pbix 文件夹路径
        file_name = os.path.basename(self.path_pbix_source)  # 文件名称包含拓展名
        demo_folder = f"{file_name[:-5]}_{time_str}"  # 新的项目文件夹
        demo_path = os.path.join(base_path, demo_folder)  # 新的项目文件夹全路径
        source_path = os.path.join(demo_path, 'source')  # source 文件夹路径
        config_path = os.path.join(source_path, 'config')  # config 文件夹路径
        data_path = os.path.join(source_path, 'data')  # data 文件夹路径
        pbit_name = f"{file_name[:-1]}t"  # pbit 名称
        self.path_pbit_target = os.path.join(demo_path, pbit_name)  # 新的项目文件夹

        # 创建文件夹
        for folder in [demo_path, source_path, config_path, data_path]:
            create_folder(folder)
        config_json_path_new = os.path.join(config_path, "config.json")  # 项目文件夹中的 config.json 路径
        shutil.copyfile(self.config_json_path, config_json_path_new)  # 复制文件到新路径
        # template 文件夹中的表格导入到 config.json 中
        dic_json = read_json(config_json_path_new)
        dic_json_tc = dic_json["TableColumns"]
        tables_template = os.path.join(self.folder_template, "tables")
        dic_tc = self.get_table_columns(special_path=tables_template)
        dic_json["TableColumns"] = {**dic_tc, **dic_json_tc}  # 相同的 key, 靠后则会保留，靠前则会被覆盖，当前就是 dic_json_tc 覆盖 dic_tc
        write_json_in_file(config_json_path_new, dic_json)
        self.folder_project = demo_path
        # return {"folder_project": demo_path}

    def create_table_in_model_tables(self) -> None:
        """复制模板中表格到模型中

        Returns:    None
        """
        tables_path = os.path.join(self.folder_temp, "Model\\tables")  # 目标模型中的 tables 文件夹路径
        tables_path_template = os.path.join(self.folder_template, "tables")  # 源模板中的 tables 文件夹路径

        # 通过循环复制源模板中的 table 到 目标模型中的 tables 文件夹中
        for folder in os.listdir(tables_path_template):
            folder_path_src = os.path.join(tables_path_template, folder)  # 源路径
            folder_path_tar = os.path.join(tables_path, folder)  # 目标路径
            if os.path.isdir(folder_path_src) and os.path.exists(folder_path_src):
                if os.path.exists(folder_path_tar):  # 如果有文件夹,先删除掉，提示那些表明为保留字
                    shutil.rmtree(folder_path_tar)
                shutil.copytree(folder_path_src, folder_path_tar)

    queries_new = []  # 新增加的 M 查询

    def create_queries_in_model_queries(self) -> None:
        """复制模板中 M 查询到模型中

        Returns:    None
        """
        queries_path = os.path.join(self.folder_temp, "Model\\queries")  # 目标模型中的 tables 文件夹路径
        queries_path_template = os.path.join(self.folder_template, "queries")  # 源模板中的 queries 文件夹路径

        # 通过循环复制源模板中的 .m 到 目标模型中的 queries 文件夹中
        for file in os.listdir(queries_path_template):
            file_path_src = os.path.join(queries_path_template, file)  # 源路径
            shutil.copy(file_path_src, queries_path)
            self.queries_new.append(file)

            if file == "Path_Sample.m":  # 如果是参数路径，需要修改项目文件路径
                path = os.path.join(queries_path, file)
                parameter_path = os.path.join(self.folder_project, "source")
                parameter_path = parameter_path.replace("/", "\\")  # 统一路径参数里面都为windows下的反斜杠 \
                self.rewrite_parameter_in_queries(path, parameter_path)

    @staticmethod
    def rewrite_parameter_in_queries(path: str, new_content: any) -> None:
        """重写 M 中的参数值

        Args:
            path (str): 需要重写的 M 查询的路径
            new_content (any): 新值

        Returns:    None
        """
        # 读取原值
        m_text = read_file_to_str(path)

        # 使用 “ meta ” 拆分，注意前后都有空格的。
        m_list = m_text.split(" meta ")
        if type(new_content) is str:
            m_list[0] = f'"{new_content}"'
        elif type(new_content) is int or type(new_content) is float:
            m_list[0] = str(new_content)
        m_text_new = " meta ".join(m_list)

        # 覆盖写回
        write_str_in_file(path, m_text_new)

    def extract_database_json_expressions(self, folder_out: str, folder_pbix_extract_x: int = None) -> list:
        """提取 database.json 中的 expressions 结点

        在 Model 文件下 expressions 文件夹 单独存放为独立 json 文件
        便于后续在 queries 文件中匹配使用。

        Args:
            folder_out (str):输出的文件夹路径
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:
            返回所有的 expressions 结点要元素的 name 列表,如果没有则返回 None
        """
        database_json_path = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), "Model\\database.json")
        database_json_dic = read_json(database_json_path)  # 读取 database.json
        path_base = os.path.join(folder_out, "expressions")  # 构造 expressions 文件夹路径

        # 循环生成 expressions 结点下的 json 文件
        if expressions := database_json_dic["model"]["expressions"]:
            expressions_name_list = []
            init_folder(path_base)  # 初始化 path_base
            for dic in expressions:
                name = dic["name"]
                expressions_name_list.append(name)
                path = os.path.join(path_base, f"{name}.json")
                write_json_in_file(path, dic)
            return expressions_name_list

    def rewrite_database_json_expressions(self) -> dict:
        """读取 template\expressions 文件夹信息，重写 database.json 的 expressions 结点

        主要是：参数 或 未加载的 m 语句 以及函数

        Returns: {"config":"config"}
            返回的查询组名称字典
        """
        expressions_name_old = {}
        database_json_dic = read_json(self.database_json_path)  # 读取 database.json
        # 判断是否存在 expressions 结点
        if "expressions" in database_json_dic["model"]:
            expressions_list = database_json_dic["model"]["expressions"]
            for exp_dic in expressions_list:
                expressions_name_old[exp_dic["name"]] = exp_dic
        else:
            expressions_list = []

        expressions_folder = os.path.join(self.folder_template, "expressions")  # 构造 expressions 路径

        qq_dict = {}  # 返回的查询组名称字典
        # 读取后添加到 expressions 结点
        for file in os.listdir(expressions_folder):
            dic = read_json(os.path.join(expressions_folder, file))
            if dic["name"] in expressions_name_old:
                expressions_list.remove(expressions_name_old[dic["name"]])
            expressions_list.append(dic)
            if "queryGroup" in dic:
                qq_dict[dic["queryGroup"]] = dic["queryGroup"]

        database_json_dic["model"]["expressions"] = expressions_list
        write_json_in_file(self.database_json_path, database_json_dic)
        return qq_dict

    def rewrite_database_json_queryGroups(self) -> None:
        """重写database.json 中的 queryGroups 结点

        注意：在 rewrite_database_json_expressions 函数之后使用。

        Returns: None

        """
        # 1、历遍 database.json 中 expressions list 中的 "queryGroup"
        qg_dict = self.rewrite_database_json_expressions()
        # 2、避免名称重复，通过历遍 Model\tables 中表的文件夹中的 table.json->["partitions"][""queryGroup"] 获取
        tables_path = os.path.join(self.folder_temp, "Model\\tables")
        for folder in os.listdir(tables_path):
            folder_path = os.path.join(tables_path, folder)
            if os.path.isdir(folder_path):
                table_json = os.path.join(folder_path, "table.json")
                dic = read_json(table_json)

                if "partitions" in dic:
                    for dic_c in dic["partitions"]:
                        if "queryGroup" in dic_c:
                            qg_name = dic_c["queryGroup"]
                            qg_dict[qg_name] = qg_name
        # 3、读取 database.json 依次写入 queryGroup
        database_json_dic = read_json(self.database_json_path)
        qg_dict_old = {}
        if "queryGroups" in database_json_dic["model"]:
            qg_list = database_json_dic["model"]["queryGroups"]
            for qg in qg_list:
                qg_dict_old[qg["folder"]] = qg["folder"]
        else:
            qg_list = []

        for index, qg in enumerate(qg_dict):
            if qg not in qg_dict_old:
                query_group = {
                        "folder"     : qg,
                        "annotations": [
                                {
                                        "name" : "PBI_QueryGroupOrder",
                                        "value": str(index)
                                }
                        ]
                }
                qg_list.append(query_group)

        # 覆盖写入
        database_json_dic["model"]["queryGroups"] = qg_list
        write_json_in_file(self.database_json_path, database_json_dic)

    def rewrite_database_json_annotations(self) -> None:
        """重写database.json annotations 结点

        Returns: None

        """

        if not (queries := self.queries_new):
            return
        queries_name = [query[:-2] for query in queries]  # 拿到新增的 m 查询的名称
        q_text = '","'.join(queries_name)  # 重组文本格式 Path","fxCalendar","fxCalendar_CN","Measure

        database_json_dic = read_json(self.database_json_path)
        if "annotations" in database_json_dic["model"]:
            annotations = database_json_dic["model"]["annotations"]
            for dic in annotations:
                if dic["name"] == "PBI_QueryOrder":
                    value_text = dic["value"][2:-2]  # 去头:[" ; 尾:"]
                    q_list_old = value_text.split('","')  # 拆成查询名称列表
                    queries_name = list(set(queries_name) - set(q_list_old) & set(queries_name))  # 去重补集
                    if queries_name:
                        q_text = '","'.join(queries_name)  # 重组文本格式 Path","fxCalendar","fxCalendar_CN","Measure
                        value = list(dic["value"])
                        value.insert(1, f'"{q_text}",')
                        dic["value"] = "".join(value)  # 字符串格式["Path","fxCalendar","fxCalendar_CN","Measure"]
                    break
            else:  # 循环完毕都没有的话执行如下格式
                annotations.append({"name": "PBI_QueryOrder", "value": f'["{q_text}"]'})
        else:
            annotations = [{"name": "PBI_QueryOrder", "value": f'["{q_text}"]'}]

        # 覆盖写入
        database_json_dic["model"]["annotations"] = annotations
        write_json_in_file(self.database_json_path, database_json_dic)

    @staticmethod
    def replace_list(key_words: str, name: str, home_name: str = None) -> list:
        """深层级的 json 使用正则替换

        Args:
            key_words (str): 替换的关键字
            name (str): 返回元组中的索引，dax=>DAX 函数，page_title=>页标题，navigation_button=>导航按钮
            home_name (str): home 页面的名称，默认是 None

        Returns:    list
            目标替换文本 list

        """
        dax = [
                {"old_text": "pageOrdinal = 0", "new_text": f"pageOrdinal = {key_words}"},
                {"old_text": "pageOrdinalYes = 0", "new_text": f"pageOrdinalYes = {key_words}"}]
        page_title = [
                {"old_text": re.compile(r"\d*\w*_pageTitleBackgroundColor"), "new_text": f"{key_words}_pageTitleBackgroundColor"},
                {"old_text": re.compile(r"\d*\w*_pageTitleText"), "new_text": f"{key_words}_pageTitleText"},
                {"old_text": re.compile(r"\d*\w*_pageTitleTextColor"), "new_text": f"{key_words}_pageTitleTextColor"}]

        if home_name:
            btn_displayname = {"old_text": re.compile(r"\d*\w*_navigationButtonDisplayName"), "new_text": f"{home_name}_navigationButtonDisplayName"}
            btn_text_color = {"old_text": re.compile(r"\d*\w*_navigationButtonTextColor"), "new_text": f"{home_name}_navigationButtonTextColor"}
            btn_background_color = {"old_text": re.compile(r"\d*\w*_navigationButtonBackgroundColor"), "new_text": f"{home_name}_navigationButtonBackgroundColor"}
        else:
            btn_displayname = {"old_text": re.compile(r"\d*\w*_navigationButtonDisplayName"), "new_text": f"{key_words}_navigationButtonDisplayName"}
            btn_text_color = {"old_text": re.compile(r"\d*\w*_navigationButtonTextColor"), "new_text": f"{key_words}_navigationButtonTextColor"}
            btn_background_color = {"old_text": re.compile(r"\d*\w*_navigationButtonBackgroundColor"), "new_text": f"{key_words}_navigationButtonBackgroundColor"}

        navigation_button = [
                btn_displayname,
                btn_text_color,
                btn_background_color,
                {"old_text": re.compile(r"\d*\w*_navigationButtonPageDisplayName"), "new_text": f"{key_words}_navigationButtonPageDisplayName"},
                {"old_text": re.compile(r"\d*\w*_navigationButtonTooltip"), "new_text": f"{key_words}_navigationButtonTooltip"}]

        dic = {"dax": dax, "page_title": page_title, "navigation_button": navigation_button}

        return dic[name]

    def folder_visualcontainers(self, page_name: str, sections_folder: str = None) -> str:
        """获取文件夹 Report/sections/page_name/visualContainers 路径

        Args:
            page_name (str): 页面名称关键字
            sections_folder (str): pbix 提取的 Report\\sections,默认为 None 即临时文件夹下的位置

        Returns: visualContainers 路径

        """

        # 获取页面
        if page_name:
            folders = os.listdir(self.folder_sections) if sections_folder is None else os.listdir(sections_folder)

            for folder in folders:
                if folder.__contains__(page_name):
                    folder_path = os.path.join(self.folder_sections, folder)
                    return os.path.join(folder_path, "visualContainers")

    def visualcontainers_visual_config_json(self, page_name: str, visual: str) -> str:
        """获取 temp/Report/sections/page_name/visualContainers/visual/config.json

        Args:
            page_name (str): 页面名称
            visual (str):视觉对象关键字或名称

        Returns: visual 对应的是config.json 路径,如果没有则是 None

        """

        vc = self.folder_visualcontainers(page_name)
        # 获取页面内的视觉对象
        for folder in os.listdir(vc):
            if folder.__contains__(visual):
                file = os.path.join(folder, "config.json")  # 页面文件夹下的视觉对象 config.json 相对路径
                file_full = os.path.join(vc, file)  # 拼接 config.json 绝对路径
                if os.path.exists(file_full):
                    return file_full

    def rewrite_content_page_visual_next_previous_config_json(self, config_json, key_words, home_page=None):
        """抽象 previous 按钮 json 重写的方法

        :param config_json: config.json 路径
        :type config_json: str
        :param key_words: 关键字
        :type key_words: str
        :param home_page: 是否要包含首页, 默认 None
        :type home_page: str
        :return: None
        :rtype: None
        """

        # 读取 config.json
        dic = read_json(config_json)

        # 深层级的 json 使用正则替换
        if home_page:
            replace_list = self.replace_list(key_words, "navigation_button", home_page)
        else:
            replace_list = self.replace_list(key_words, "navigation_button")

        # 转换成 json 文本
        json_text = json.dumps(dic, ensure_ascii=False)

        # 循环替换
        for r_dic in replace_list:
            json_text = r_dic["old_text"].sub(r_dic["new_text"], json_text)

        # 覆盖写入
        dic = json.loads(json_text)
        write_json_in_file(config_json, dic)

    def rewrite_visualcontainer_json(self, page_count, index, visualcontainer_json, config_json, ordinal, displayName):
        """抽象出视觉对象 visualcontainer.json 重写的方法

        :param page_count: 页面数量
        :type page_count: int
        :param index: 按钮索引
        :type index: int
        :param visualcontainer_json: json 文件路径
        :type visualcontainer_json: str
        :param config_json: config.json 路径
        :type config_json: str
        :param ordinal: 页面序号
        :type ordinal: int
        :param displayName: 页面名称
        :type displayName: str
        :return: None
        :rtype: None
        """

        name_nb = f"'NB_{displayName}'"
        name_prefix = f"{str(ordinal).zfill(3)}_{displayName}"

        # 自定义函数,算出每个按钮的信息：{'x': 16, 'y': 1182, 'z': 18912, 'width': 168, 'height': 64, 'tabOrder': 18912}
        position_dic = self.get_button_position_dic(page_count, index)

        # 重写视觉对象里面的 json 首先写入 visualcontainer_json
        write_json_in_file(visualcontainer_json, position_dic)

        # 读取 config.json
        dic = read_json(config_json)

        # 修改 name
        dic["name"] = name_prefix
        # 修改布局的信息
        dic["layouts"][0]["position"] = position_dic
        # 按钮名称
        dic["singleVisual"]["vcObjects"]["title"][0]["properties"]["text"]["expr"]["Literal"]["Value"] = name_nb

        # 深层级的 json 使用正则替换
        replace_list = self.replace_list(name_prefix, "navigation_button")

        # 转换成 json 文本
        json_text = json.dumps(dic, ensure_ascii=False)

        # 循环替换
        for r_dic in replace_list:
            json_text = r_dic["old_text"].sub(r_dic["new_text"], json_text)

        # 覆盖写入
        dic = json.loads(json_text)
        write_json_in_file(config_json, dic)

    def read_report_page_json(self):
        """读取 初始化后的 config.json

        如果列表中只有1个元素则导航页使用总导航页 Navigation,移除 A00
        :return: {"pages": pages, "name_group": name_group}
        :rtype: dict
        """

        # 读取基础报表配置
        dic = read_json(self.config_json_path)
        measure_is_hidden = dic["TemplateMeasureIsHidden"]
        pages = dic["ReportPages"]
        page_list = dic["PageGroup"] or self.new_report_pages_list
        # 新增页面名称组多组list [['A00', 'A01', 'A02', 'A03', 'A04'], ['B00', 'B01', 'B02'], ['C00', 'C01', 'C02']]
        name_group = []

        # 循环得到每页报表的 name
        ordinal = 2
        for page_g in page_list:
            name_x = []
            for _ in range(page_g):
                ordinal += 1
                for page in pages:
                    if int(page["ordinal"]) == ordinal:
                        name_x.append({"ordinal": ordinal, "name": page["displayName"]})
                        break
            name_group.append(name_x)

        return {"pages": pages, "name_group": name_group, "measure_is_hidden": measure_is_hidden}

    def get_measure_table(self, folder_pbix_extract_x: int = None):
        """获取存放度量值的表

        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :return: 表名的列表
        :rtype: list
        """

        path = "Model\\tables"
        path = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), path)

        folders = os.listdir(path)
        return [folder for folder in folders if os.path.exists(os.path.join(os.path.join(path, folder), "measures"))]

    def get_table_columns(self, folder_pbix_extract_x: int = None, special_path: str = None) -> dict:
        """获取模型中的表和列

        Args:
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空
            special_path (str): 特殊路径，默认 None, 有路径时候，按照特殊路径下获取表和列的字典

        Returns:表和列的字典

        """

        path = "Model\\tables"
        path = os.path.join(self.folder_pbix_extract(folder_pbix_extract_x), path)
        if special_path:
            path = special_path
        folders = os.listdir(path)
        dict0 = {}
        for item in folders:
            path_children = os.path.join(path, item)
            if os.path.isdir(path_children):
                path_columns = os.path.join(path_children, "columns")
                if os.path.exists(path_columns):
                    col_list = []
                    for col in os.listdir(path_columns):
                        col_file = os.path.join(path_columns, col)
                        if os.path.isfile(col_file):
                            col_name = col.split(".json")[0]
                            col_list.append(col_name)
                    dict0[item] = col_list
        return dict0

    def get_all_tables(self, folder_pbix_extract_x: int = None):
        """获取所有表格名称的列表

        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :return: 表名的列表
        :rtype: list
        """
        measure_table = self.get_measure_table(folder_pbix_extract_x)
        data_table = self.get_table_columns(folder_pbix_extract_x)
        for table in data_table:
            if table not in measure_table:
                measure_table.append(table)
        return measure_table

    def get_measures(self, keyword=".dax", folder_pbix_extract_x: int = None):
        """获取提起后的度量值信息

        :param keyword: 文件名称后缀关键字, 默认 ".dax"
        :type keyword:str
        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :return:示例:"table->name":{"name": name, "folder": path_measure_table, "table": table, "dax": dax, "json": json}
        :rtype:dict
        """
        keyword = keyword
        count = len(keyword)
        measure_tables = self.get_measure_table(folder_pbix_extract_x)
        path = self.folder_pbix_extract(folder_pbix_extract_x)
        daxs = {}
        for table in measure_tables:
            path_measure_table = self.set_measures_folder(path, table)
            file_list = os.listdir(path_measure_table)
            for file in file_list:
                if file[-count:] == keyword:
                    name = file[:-count]
                    dax = os.path.join(path_measure_table, file)
                    json_file = os.path.join(path_measure_table, f"{file[:-4]}.json")
                    key = f"{table}->{name}"
                    daxs[key] = {"name": name, "folder": path_measure_table, "table": table, "dax": dax, "json": json_file}
        return daxs

    def measure_import_pbixa_2_pbixb(self, table_target, file_src_list, folder_pbix_extract_x: int = None):
        """度量值从 pbixA 导入 pbixB

        :param table_target: 导入的目标表格
        :type table_target: str
        :param file_src_list: 文件列表
        :type file_src_list: list
        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :return: None
        :rtype: None
        """
        # TODO 删除重复度量值待定完善
        path_target = self.folder_pbix_extract(folder_pbix_extract_x)
        path_measure_table_target = self.set_measures_folder(path_target, table_target)
        # 判断是否存在,不存在就创建
        create_folder(path_measure_table_target)
        if file_src_list:
            for file_src in file_src_list:
                shutil.copy(file_src, path_measure_table_target)

    def measure_import_folder_2_pbix(self, table_target: str, folder_src: str, folder_pbix_extract_x: int = None) -> None:
        """度量值导入到 pbix 提取的文件夹中

        Args:
            table_target (str): 提取的文件夹 tables 中表名的文件夹名称(不是绝对路径)
            folder_src (str):度量值存放的文件夹绝对路径
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:None

        """

        delimiter = "]["
        measure_attributes = ["description", "displayFolder", "formatString", "dataCategory"]
        # 1、新建临时文件夹
        measure_temp_folder = os.path.join(os.getenv("TEMP"), "measures_import")
        file_src_list = []
        init_folder(measure_temp_folder)
        # 2、根据文件名称拆分得到 度量值主表 和 name
        files = os.listdir(folder_src)
        measure_name_list = []  # 导入的度量值名称列表
        for file in files:
            dic = {}
            file_path = os.path.join(folder_src, file)
            if os.path.isfile(file_path) and file[-4:] == ".dax":
                name = file[:-4]
                if delimiter in name:
                    name = name.split(delimiter)[-1]
                dic["name"] = name
                measure_name_list.append(name)
                dax = read_file_to_str(file_path)
                # 根据注释文档中的度量值属性，重写度量值的属性 json
                if comment := re.findall(r"^/\*.*?\*/", dax, flags=re.S):  # 正则读取最前面的注释文档
                    if line_list := re.findall(r"^@.*", comment[0], flags=re.MULTILINE):  # 对注释文档再拆分，注释 flags 的参数
                        for line in line_list:
                            key_value = line[1:].split(":")
                            key = key_value[0]
                            if key in measure_attributes:
                                dax = dax.replace(comment[0], "")  # 当注释文档是设置属性才对文档进行替换删除以及后续动作
                                dic[key] = key_value[1]
                            if key == "formatString":  # 当度量值有格式化字符串才有如下属性
                                annotations = [{"name" : "PBI_ChangedProperties",
                                                "value": "[\"FormatString\"]"}]
                                dic["annotations"] = annotations

                dax_path_new = os.path.join(measure_temp_folder, f"{name}.dax")
                # 3、生成 json 配置文件
                json_path = os.path.join(measure_temp_folder, f"{name}.json")
                write_json_in_file(json_path, dic)
                # 4、写入dax
                write_str_in_file(dax_path_new, dax)
                file_src_list.append(dax_path_new)
                file_src_list.append(json_path)
        # 5、导入到目标文件夹
        self.remove_repeat_measure(measure_name_list, folder_pbix_extract_x=folder_pbix_extract_x)  # 删除重复的度量值，
        self.measure_import_pbixa_2_pbixb(table_target, file_src_list, folder_pbix_extract_x)

    def measure_export_pbix_2_folder(self, folder_target, folder_pbix_extract_x: int = None) -> None:
        """导出 dax 表达式，包含 dax 属性

        Args:
            folder_target (str): 导出的文件夹
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:None

        """
        delimiter = "]["
        # measures = {"name": name, "folder": path_measure_table, "table": table, "dax": dax, "json": json}
        measures = self.get_measures(folder_pbix_extract_x=folder_pbix_extract_x)
        measure_attributes = ["description", "displayFolder", "formatString", "dataCategory"]
        for measure in measures:
            # 1、读取json 准备好名称
            name_list = [measures[measure]["table"], measures[measure]["name"]]
            json_dic = read_json(measures[measure]["json"])
            comment = "/*"
            for attribute in measure_attributes:
                if attribute in json_dic:
                    value = f"\n@{attribute}:{json_dic[attribute]}"
                    comment += value
            comment += "\n*/\n"

            new_name = delimiter.join(name_list)
            new_name_path = f'{os.path.join(folder_target, new_name)}.dax'

            dax = read_file_to_str(measures[measure]["dax"])
            if comment != "/*\n*/\n":
                dax = comment + dax
            write_str_in_file(new_name_path, dax)

    def write_report_visual_template_measures(self):
        """写入模板度量值，即ui_02 页面的度量值

        :return: None
        :rtype: None
        """
        img_tmp = DAX_REPORT_VISUAL_TEMPLATE["ImageUrl"]
        img_tmp_not = DAX_REPORT_VISUAL_TEMPLATE["NotImageUrl"]
        config_json_dic = read_json(self.config_json_path)
        measure_is_hidden = self.read_report_page_json()["measure_is_hidden"]
        if measure_list := config_json_dic["ReportVisualTemplates"]:

            measure_name_keyword_list = [dic["name"] for dic in measure_list]
            # 删除重复的度量值，绝对匹配模式
            self.remove_repeat_measure(measure_name_keyword_list)
            # 写入新度量值
            for measure in measure_list:
                if measure["dataCategory"] == "ImageUrl":
                    dax_template = Template(img_tmp)
                else:
                    dax_template = Template(img_tmp_not)

                visual_dax = dax_template.substitute(measure)
                # 度量值名称
                name = measure["name"]
                # 度量值文件夹
                if measure["displayFolder"] != "":
                    displayfolder = measure["displayFolder"]
                else:
                    displayfolder = "config\\visual"
                # 度量值类型
                dataCategory = measure["dataCategory"]
                # 度量值描述说明
                description = measure["description"]
                # measure_folder = measure["measureTable"]  # measureTable = self.name_measure_table, 不单独设置
                # 度量值信息配置的 json 文件内容
                json_dax = {"name": name, "description": description, "displayFolder": displayfolder, "dataCategory": dataCategory}
                # 度量值是否隐藏
                json_dax = self.measure_is_hidden_dict(json_dax, measure_is_hidden)
                # 度量值信息配置的 json 文件名称
                json_name = f"{name}.json"
                # 度量值名称
                dax_name = f"{name}.dax"
                # 覆盖写入导航度量值
                dax_folder = self.set_measures_folder()
                create_folder(dax_folder)
                write_str_in_file(os.path.join(dax_folder, dax_name), visual_dax)
                write_json_in_file(os.path.join(dax_folder, json_name), json_dax)

    def write_rls_measures(self):
        """写入 RLS 度量值

        :return: None
        :rtype: None
        """
        config_json_dic = read_json(self.config_json_path)
        measure_is_hidden = self.read_report_page_json()["measure_is_hidden"]
        if permission_list := config_json_dic["PermissionList"]:

            measure_name_keyword_list = [dic["name"] for dic in permission_list]
            # 删除重复的度量值，绝对匹配模式
            self.remove_repeat_measure(measure_name_keyword_list)

            for permission in permission_list:
                # 从 tableColumn 拆除表名称, 例子：'D00_大区表'[F_01_大区ID]
                table_name = permission["tableColumn"].split("[")[0]
                permission["tableName"] = table_name
                rls_template = Template(RLS_DAX_TEMPLATE)
                rls_dax = rls_template.substitute(permission)
                # 度量值名称
                name = permission["name"]
                # 度量值文件夹
                displayfolder = f"{self.name_dax_folder_parent}\\rls"
                # 度量值信息配置的 json 文件内容
                json_dax = {"name": name, "displayFolder": displayfolder}
                # 度量值是否隐藏
                json_dax = self.measure_is_hidden_dict(json_dax, measure_is_hidden)
                # 度量值信息配置的 json 文件名称
                json_name = f"{name}.json"
                # 度量值名称
                dax_name = f"{name}.dax"
                # 覆盖写入导航度量值
                dax_folder = self.set_measures_folder()
                create_folder(dax_folder)
                write_str_in_file(os.path.join(dax_folder, dax_name), rls_dax)
                write_json_in_file(os.path.join(dax_folder, json_name), json_dax)

    def rewrite_database_json_rls(self):
        """重写 \Model\database.json 关于 RLS 部分

        # roles = {"name": "RLS", "modelPermission": "read", "tablePermissions": []}
        # tablePermission = {"name": "T01_门店表", "filterExpression": "[rlsOrganization]"}

        :return: None
        :rtype: None
        """

        database_json_dic = read_json(self.database_json_path)
        config_json_dic = read_json(self.config_json_path)

        if permission_list := config_json_dic["PermissionList"]:
            table_dic = {}  # {"table": []}
            for permission in permission_list:
                table = permission["tableColumn"].split("'")[1]
                if table not in table_dic:
                    table_dic[table] = []
                table_dic[table].append(permission)

            role = {
                    "name"            : "rls",
                    "modelPermission" : "read",
                    "tablePermissions": [],
                    "annotations"     : [{
                            "name" : "PBI_Id",
                            "value": str(uuid.uuid4())
                    }]
            }

            for table, value in table_dic.items():
                filterExpression_list = [f'[{permission["name"]}]' for permission in value]
                rls_content = {"name": table, "filterExpression": "&&".join(filterExpression_list)}  # 注意 dax &&连接符号
                role["tablePermissions"].append(rls_content)
            roles = [role]
            database_json_dic["model"]["roles"] = roles
            write_json_in_file(self.database_json_path, database_json_dic)

    def remove_repeat_measure(self,
                              measure_name_list: list,
                              is_contain: bool = False,
                              path: str = None,
                              folder_pbix_extract_x: int = None) -> None:
        """移除重复度量值

        Args:
            measure_name_list (list): 度量值名称的关键字形成的列表,也可以是度量值名称列表。
            is_contain (bool, optional): 是否启用关键字包含模式;默认值:False.
            path (str): 文件夹路径
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns: None

        """

        # 保证有度量表才删除
        if old_measure_tables := self.get_measure_table(folder_pbix_extract_x):
            for mt in old_measure_tables:
                for measure_name in measure_name_list:
                    files = os.listdir(self.set_measures_folder(path=path, folder=mt))

                    for file in files:
                        if is_contain and file.lower().__contains__(measure_name.lower()):
                            os.remove(os.path.join(self.set_measures_folder(path=path, folder=mt), file))
                        elif not is_contain and file.lower()[:-4] == measure_name.lower():
                            os.remove(os.path.join(self.set_measures_folder(path=path, folder=mt), file))
                        elif not is_contain and file.lower()[:-5] == measure_name.lower():
                            os.remove(os.path.join(self.set_measures_folder(path=path, folder=mt), file))

    # 三、方法

    def pbi_tools_command_info(self):
        """info检查pbi是否启动

        :return: {pbi["PbixPath"]: pbi for pbi in pbiSessions} ,没有启动则是空列表
        :rtype: dict
        """

        # command 命令
        pbi_tools_command_info = f'"{self.exe_pbi_tools}" info'
        # 执行提取命令
        text_out = execCmd(pbi_tools_command_info)
        dic = json.loads(text_out, strict=False)

        # pbiSessions 字典列表列表
        # [{
        #     "ProductName": "Microsoft Power BI Desktop",
        #     "ProductVersion": "2.110.805.0 (22.10)",
        #     "ExePath": "C:\\Program Files\\WindowsApps\\Microsoft.MicrosoftPowerBIDesktop_2.110.805.0_x64__8wekyb3d8bbwe\\bin\\pbidesktop.exe",
        #     "ProcessId": 16572,
        #     "Port": 5104,
        #     "WorkspaceName": "AnalysisServicesWorkspace_168174a0-a546-4a4b-acbb-4fcd3f8a2821",
        #     "WorkspaceDir": "C:\\Users\\jiaopengzi\\Microsoft\\Power BI Desktop Store App\\AnalysisServicesWorkspaces\\AnalysisServicesWorkspace_168174a0-a546-4a4b-acbb-4fcd3f8a2821\\Data",
        #     "PbixPath": "C:\\desktop\\category\\category.pbix"
        # }]
        if pbiSessions := dic["pbiSessions"]:
            return {pbi["PbixPath"]: pbi for pbi in pbiSessions}
        else:
            return {}

    def pbi_tools_command_extract(self, folder_pbix_extract_x: int = None, pid=None, watch=False):
        """提取 PBIX 模板信息 pbi_tools_command_extract()

        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :param pid: 进程编号
        :type pid: int
        :param watch: 是否查看模式，默认 False
        :type watch: bool
        :return: 命令行输出
        :rtype: str
        """

        pid = f"-pid {pid} " if pid else ""
        watch = "-watch " if watch else ""
        # 文件夹重置

        folder = self.folder_pbix_extract(folder_pbix_extract_x, create=True)
        init_folder(folder)
        # command 命令
        pbi_tools_command_extract = f'"{self.exe_pbi_tools}" extract {pid}{watch}-pbixPath "{self.path_pbix_source}" -extractFolder "{folder}"'
        out = execCmd(pbi_tools_command_extract)
        if not os.path.getsize(folder):  # 为空
            self.folder_pbix_extract_status = False
        return out

    def init_config_json(self, measure_is_hidden: bool = False):
        """初始化 config.json

        如果列表中只有1个元素则导航页使用总导航页 Navigation, 移除 A00
        新增页面名称组多组list [['A00', 'A01', 'A02', 'A03', 'A04'], ['B00', 'B01', 'B02'], ['C00', 'C01', 'C02']]
        一组list ['A00', 'A01', 'A02', 'A03', 'A04', 'B00', 'B01', 'B02', 'C00', 'C01', 'C02']

        :param measure_is_hidden: 模板度量值度量是否隐藏
        :return: None
        """

        rpbj = set_custom_report_base(REPORT_PAGE_BASE_JSON) or REPORT_PAGE_BASE_JSON
        rvtb = set_custom_report_visual_templates_base(REPORT_VISUAL_TEMPLATES_BASE) or REPORT_VISUAL_TEMPLATES_BASE

        page_list = self.new_report_pages_list
        sub = len(page_list)
        a_z = [chr(i) for i in range(65, 91)]

        names = []

        # 循环得到每页报表的 name
        # ordinal = 2
        for index, page_g in enumerate(page_list):
            for i in range(page_g):
                if sub == 1:
                    name = a_z[index] + str(i + 1).zfill(2)
                else:
                    name = a_z[index] + str(i).zfill(2)
                names.append(name)

        dic = copy.deepcopy(rpbj)  # 深拷贝

        for ordinal in [1, 2]:
            # 处理前两页默认页面
            dic["ReportPages"][ordinal]["name"] = self.get_report_name_id(dic["ReportPages"][ordinal]["name"])

        # 处理新增页面
        for ordinal, name in enumerate(names, start=3):
            # 注意使用复制字典
            dic_base = dic["ReportPages"][0].copy()
            # 循环修改字典的键值
            dic_base["ordinal"] = ordinal
            dic_base["name"] = self.get_report_name_id(name)
            dic_base["visibility"] = 1
            dic_base["displayName"] = name
            dic_base["pageTitleText"] = f"{name} title"
            dic_base["navigationButtonName"] = f"NB_{name}"
            dic_base["navigationButtonDisplayName"] = name
            dic_base["navigationButtonTooltipYes"] = name

            # 列表中增加
            dic["ReportPages"].append(dic_base)

        userdomain = os.environ['userdomain']
        username = os.environ['USERNAME']
        user_principal_name_local = f"{userdomain}\\{username}"
        # page_ordinal_list = [str(page["ordinal"]) for page in dic["ReportPages"]]
        page_ordinal_list = [page["ordinal"] for page in dic["ReportPages"]]
        dic["PowerBIUsers"] = [
                {
                        "userID"           : "1001",
                        "userName"         : username,
                        "userPrincipalName": [user_principal_name_local],
                        "roles"            : [
                                {
                                        "permissionName": "reportPage",
                                        # "dimension": "reportPage",
                                        "dimension"     : "ordinal",
                                        "value"         : page_ordinal_list
                                }
                        ]
                }
        ]

        dic["ReportVisualTemplates"] = rvtb
        dic["PageGroup"] = page_list
        dic["MeasureTable"] = self.get_all_tables()
        dic["TableColumns"] = self.get_table_columns()
        dic["PermissionList"] = []
        dic["TemplateMeasureIsHidden"] = measure_is_hidden
        # 覆盖写入
        write_json_in_file(self.config_json_path, dic)

    def init_folder_temp(self, folder_pbix_extract_x: int = None):
        """初始化 temp 文件夹

        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :return: None
        :rtype: None
        """
        if os.path.exists(self.folder_temp):  # 存在则删除
            shutil.rmtree(self.folder_temp)
        shutil.copytree(self.folder_pbix_extract(folder_pbix_extract_x), self.folder_temp)  # 复制对应的文件夹并更名为需要的文件夹名称

    @staticmethod
    def measure_is_hidden_dict(measure_dict, measure_is_hidden: bool = False) -> dict:
        """度量值是否隐藏字典

        :param measure_dict: 度量值属性字典
        :param measure_is_hidden: 是否隐藏度量值
        :return: 度量值属性字典
        """
        if measure_is_hidden:
            measure_dict["isHidden"] = True
            measure_dict["changedProperties"] = [{"property": "IsHidden"}]
        return measure_dict

    def rewrite_dax_from_template_dax_list(self):
        """源自 TEMPLATE_DAX_LIST 根据页面信息写入导航的度量值。

        :return: None
        :rtype: None
        """
        pages = self.read_report_page_json()["pages"]
        measure_is_hidden = self.read_report_page_json()["measure_is_hidden"]
        # 迁移至config
        dax_dic_list = TEMPLATE_DAX_LIST
        measure_name_keyword_list = list(TEMPLATE_DAX_LIST.keys())

        self.remove_repeat_measure(measure_name_keyword_list, True)  # 删除重复的度量值，关键字模式

        for page in pages:
            # 拼接替换的内容
            replace_list = self.replace_list(str(page["ordinal"]), "dax")
            # 循环拼接名称并读取对应的 DAX
            for dax_dic in dax_dic_list:
                # 读取 dax
                dax = dax_dic_list[dax_dic]
                # 度量值名称
                name = f"{str(page['ordinal']).zfill(3)}_{page['displayName']}_{dax_dic}"
                # 度量值文件夹
                displayfolder = f"{self.name_dax_folder_parent}\\{dax_dic}"
                # 度量值信息配置的 json 文件内容
                json_dax = {"name": name, "displayFolder": displayfolder}
                # 度量值是否隐藏
                # measure_is_hidden = self.read_report_page_json()["measure_is_hidden"]
                json_dax = self.measure_is_hidden_dict(json_dax, measure_is_hidden)
                # 度量值信息配置的 json 文件名称
                json_name = f"{name}.json"
                # 度量值名称
                dax_name = f"{name}.dax"
                # 循环替换得到 DAX 具体内容
                for r_dic in replace_list:
                    dax = dax.replace(r_dic["old_text"], r_dic["new_text"])
                dax_folder = self.set_measures_folder()
                create_folder(dax_folder)

                # 覆盖写入导航度量值
                write_str_in_file(os.path.join(dax_folder, dax_name), dax)
                write_json_in_file(os.path.join(dax_folder, json_name), json_dax)

    def generate_report_page(self, visual_list: list) -> None:
        """生成页面，修改视觉对象的度量值配置

        Args:
            visual_list (list): 视觉对象列表，例如：["PageTitle"]

        Returns: None

        """
        self.mark_custom_old_page()  # 打标用户其它页，并且排序到最后
        self.generate_report_page_home()  # 生成首页
        self.generate_report_page_content()  # 生成内容
        self.generate_report_page_no_permission()  # 生成无权限页
        self.rewrite_sections_page_visuals_config_json_general("page_title", visual_list)  # 替换通用的视觉对象度量值配置
        self.rewrite_page_0_2_visual_next_previous_config_json()  # 首页导航无权限三页视觉对象替换
        self.rewrite_page_content_visual_previous_config_json()  # 内容也导航按钮替换

    def mark_custom_old_page(self) -> None:
        """打标用户原来的页面内容

        在页面文件夹第一个数字更改为 9；让排序到最后
        更改 section.json 中 ordinal 排序数字

        Returns:None
        """
        pages = self.read_report_page_json()["pages"]
        target = self.folder_sections
        folder_list = os.listdir(target)
        ordinal = len(pages)
        for folder in folder_list:
            if not any(folder.__contains__(page["displayName"]) for page in pages):  # 和 config 中所页面名称对比没有匹配的
                old = os.path.join(target, folder)
                new = os.path.join(target, f"9{folder[1:]}")
                os.rename(old, new)  # 重命名
                section = os.path.join(new, "section.json")
                dic = read_json(section)
                dic["ordinal"] = ordinal  # 更改排序数字
                ordinal += 1
                write_json_in_file(section, dic)  # 覆盖写会

    def generate_report_page_home(self) -> None:
        """生成首页

        Returns:None

        """
        source = self.folder_home_page()
        page_home = self.read_report_page_json()["pages"][0]
        target = self.folder_sections

        folder_list = os.listdir(target)
        for folder in folder_list:
            if folder.__contains__(page_home["displayName"]):
                tree = os.path.join(target, folder)
                shutil.rmtree(tree)  # 删除原来页面

        folder = f"{str(page_home['ordinal']).zfill(3)}_{page_home['displayName']}"  # 页面文件夹名称
        page_target = os.path.join(self.folder_sections, folder)
        shutil.copytree(source, page_target)  # 复制对应的文件夹并更名为需要的文件夹名称

    def generate_report_page_no_permission(self) -> None:
        """生成无权限页

        Returns:None

        """
        source = self.folder_no_permission_page()
        page_no_permission = self.read_report_page_json()["pages"][2]
        target = self.folder_sections

        folder_list = os.listdir(target)
        for folder in folder_list:
            if folder.__contains__(page_no_permission["displayName"]):
                tree = os.path.join(target, folder)
                shutil.rmtree(tree)  # 删除原来页面

        folder = f"{str(page_no_permission['ordinal']).zfill(3)}_{page_no_permission['displayName']}"  # 页面文件夹名称
        page_target = os.path.join(self.folder_sections, folder)
        shutil.copytree(source, page_target)  # 复制对应的文件夹并更名为需要的文件夹名称

    def generate_report_page_content(self) -> None:
        """根据 ReportPages.json 配置文件,复制文件夹,生成需要新增的报表页面

        Returns:None

        """

        pages = self.read_report_page_json()["pages"]  # 所有页面信息
        source = self.folder_content_page()  # 内容页路径
        target = self.folder_sections  # 获取报表页的文件夹路径

        # 删除原来页面
        for page in pages[1:]:
            if int(page["ordinal"]) not in [2]:  # 跳过无权限页面
                folder_list = os.listdir(target)
                for folder in folder_list:
                    if folder.__contains__(page["displayName"]):
                        tree = os.path.join(target, folder)
                        shutil.rmtree(tree)

                folder = f"{str(page['ordinal']).zfill(3)}_{page['displayName']}"  # 页面文件夹名称
                page_target = os.path.join(target, folder)
                shutil.copytree(source, page_target)  # 复制对应的文件夹并更名为需要的文件夹名称

    def rewrite_sections_page_visuals_config_json_general(self, replace_name: str, visual_list: list) -> None:
        """修改页面文件下视觉对象的 config.json,通用视觉对象（不包含导航按钮）

        Args:
            replace_name (str): 替换关键字。
            visual_list (list): 视觉对象在选择窗格里面的名称，使用列表形式。

        Returns:None

        """

        pages = self.read_report_page_json()["pages"]
        for page in pages:
            name_prefix = f"{str(page['ordinal']).zfill(3)}_{page['displayName']}"  # 页面名称也是度量值前缀
            replace_list = self.replace_list(name_prefix, replace_name)  # 替换字典中，需要替换的列表对象名称
            for visual in visual_list:
                if path := self.visualcontainers_visual_config_json(name_prefix, visual):
                    dic = read_json(path)  # 读取 config.json

                    dic["name"] = name_prefix + visual  # 修改名称
                    json_str = json.dumps(dic, ensure_ascii=False)  # 字典转成 json 文本
                    # 使用正则循环替换
                    for r_dic in replace_list:
                        json_str = r_dic["old_text"].sub(r_dic["new_text"], json_str)
                    dic = json.loads(json_str)  # loads 为字典类型
                    write_json_in_file(path, dic)  # 覆盖写入

    def rewrite_report_json(self):
        """重写 temp->Report->report.json

        字典中 pods list 按照页面数量重写。

        :return None
        :rtype None
        """
        pages = self.read_report_page_json()["pages"]
        report_json = self.path_report_json

        dic = read_json(report_json)  # 读取 report.json

        # 准别文件内容中 pods 空列表备用
        pods_list = []

        # 循环所有页面 构建 pods
        for page in pages:
            # 报表名称统一,随机值 uuid, 构建 pods
            pods = {
                    "boundSection": page["name"],
                    "config"      : '{"acceptsFilterContext":1}',
                    "name"        : "Pod" + str(page["ordinal"]).zfill(2)
            }
            pods_list.append(pods)

        # 替换 pods
        dic["pods"] = pods_list
        write_json_in_file(report_json, dic)  # 重写覆盖写入 report.json

    def rewrite_page_section_json(self):
        """重写 temp/Report/sections/xxx_xxx/section.json

        :return None
        :rtype None
        """
        pages = self.read_report_page_json()["pages"]
        sections = self.folder_sections
        # 循环所有页面
        for page in pages:
            # 构建 section.json 文件路径
            folder = f"{str(page['ordinal']).zfill(3)}_{page['displayName']}"
            folder = os.path.join(sections, folder)
            sections_json = os.path.join(folder, "section.json")

            # 重写 section.json 内容
            dic = {
                    "displayName"  : page["displayName"],
                    "displayOption": page["displayOption"],
                    "height"       : page["height"],
                    "name"         : page["name"],
                    "ordinal"      : page["ordinal"],
                    "width"        : page["width"],
            }
            # 覆盖写入
            write_json_in_file(sections_json, dic)

    def rewrite_page_config_json(self):
        """重写 temp/Report/sections/xxx_xxx/config.json

        :return None
        :rtype None
        """
        pages = self.read_report_page_json()["pages"]
        sections = self.folder_sections
        # 循环所有页面
        for page in pages:
            # 构建 temp/Report/sections/xxx_xxx/config.json 文件路径
            folder = f"{str(page['ordinal']).zfill(3)}_{page['displayName']}"
            folder = os.path.join(sections, folder)
            config_json = os.path.join(folder, "config.json")

            # 读取 temp/Report/sections/xxx_xxx/config.json 文件路径
            dic = read_json(config_json)

            # 多层嵌套赋值
            dic["objects"]["displayArea"][0]["properties"]["verticalAlignment"]["expr"]["Literal"]["Value"] = page["verticalAlignment"]
            dic["visibility"] = page["visibility"]

            # 覆盖写入
            write_json_in_file(config_json, dic)

    def rewrite_page_0_2_visual_next_previous_config_json(self) -> None:
        """重写模板前三页的下页和上一页按钮

        0：首页 temp/Report/sections/xxx_page/visualContainers/Next/config.json
        1：总导航页 temp/Report/sections/xxx_page/visualContainers/xxx_Previous/config.json
        2：无权限页 temp/Report/sections/xxx_page/visualContainers/xxx_Previous/config.json

        Returns:None
        """
        pages = self.read_report_page_json()["pages"]
        # 1、首页
        home = pages[0]
        home_displayname = home["displayName"]
        home_ordinal = home["ordinal"]
        home_name_prefix = f"{str(home_ordinal).zfill(3)}_{home_displayname}"
        # 2、总导航页
        navigation = pages[1]
        navigation_displayname = navigation["displayName"]
        navigation_ordinal = navigation["ordinal"]
        navigation_name_prefix = f"{str(navigation_ordinal).zfill(3)}_{navigation_displayname}"

        # 3、无权限页面
        no_permission = pages[2]
        no_permission_displayname = no_permission["displayName"]

        # 视觉对象名称
        next = "Next"
        previous = "Previous"

        # 拿到页面导航按钮 config.json 文件路径
        config_json_home = self.visualcontainers_visual_config_json(home_displayname, next)  # 首页
        config_json_navigation = self.visualcontainers_visual_config_json(navigation_displayname, previous)  # 导航
        config_json_no_permission = self.visualcontainers_visual_config_json(no_permission_displayname, previous)  # 无权限
        # 重写 config.json
        self.rewrite_content_page_visual_next_previous_config_json(config_json_home, navigation_name_prefix, home_name_prefix)  # 首页
        self.rewrite_content_page_visual_next_previous_config_json(config_json_navigation, home_name_prefix)  # 导航
        self.rewrite_content_page_visual_next_previous_config_json(config_json_no_permission, navigation_name_prefix)  # 无权限

    def rewrite_page_content_visual_previous_config_json(self):
        """ 重置绑定所有页面返回按钮的度量值

        非首页  temp/Report/sections/xxx_page/visualContainers/xxx_Previous/config.json
        模板中设置了首页和导航页的导航按钮,只需要设置后面页面即可。

        :return None
        :rtype None
        """

        visual = "Previous"
        pages = self.read_report_page_json()["pages"]
        content_page = self.read_report_page_json()["name_group"]

        displayname = pages[1]["displayName"]  # 总导航页名称
        ordinal = pages[1]["ordinal"]  # 总导航页 ordinal
        name_prefix = f"{str(ordinal).zfill(3)}_{displayname}"

        count = len(content_page)

        for page_list in content_page:
            for index_page, page in enumerate(page_list):
                config_json = self.visualcontainers_visual_config_json(page["name"], visual)

                # 重写 config.json
                if count > 1 and index_page > 0:
                    name_prefix_2 = f"{str(page_list[0]['ordinal']).zfill(3)}_{page_list[0]['name']}"
                    self.rewrite_content_page_visual_next_previous_config_json(config_json, name_prefix_2)
                else:
                    self.rewrite_content_page_visual_next_previous_config_json(config_json, name_prefix)

    def rewrite_all_page_visualcontainers_visual_config_json_measure_table_change(self, old_measure_table: str, new_measure_table: str) -> None:
        """度量值更换表后,修改每个页面下视觉对象度量值表名称

        Args:
            old_measure_table (str):原来的度量值的表名称
            new_measure_table (str):更换后的度量值表名称

        Returns:None
        """
        # 格式化替换的内容
        mt_old = f': "{old_measure_table}"'
        mt_new = f': "{new_measure_table}"'

        # 循环获取页面下的视觉对象的config.json
        page_folders = os.listdir(self.folder_sections)
        for page_folder in page_folders:
            vc_path = self.folder_visualcontainers(page_folder)
            if os.path.exists(vc_path):
                visual_folders = os.listdir(vc_path)
                for visual_folder in visual_folders:
                    visual_config_json = os.path.join(vc_path, f"{visual_folder}\\config.json")
                    self.file_text_replace(visual_config_json, mt_old, mt_new)  # 替换关于度量值表格的内容

    @staticmethod
    def file_text_replace(file_path: str, old: str, new: str) -> None:
        """文件文本内容替换

        Args:
            file_path (str): 需要替换的文件路径
            old (str): 需要被替换的老值
            new (str): 替换后的新值

        Returns:    None
        """
        file_text = read_file_to_str(file_path)  # 读取
        file_text = file_text.replace(old, new)  # 替换
        write_str_in_file(file_path, file_text)  # 覆盖写入

    def copy_navigation_button(self, btn_key_world: str = "NB_") -> None:
        """复制导航按钮

        :param btn_key_world: 需要覆盖删除导航按钮关键字 默认 "NB_"
        :type btn_key_world: str
        :return: None
        :rtype: None
        """

        source = self.folder_navigation_button
        report_page = self.read_report_page_json()
        navigation_page_display_name = report_page["pages"][1]["displayName"]
        content_page = report_page["name_group"]

        count = len(content_page)
        for page_list in content_page:
            for index_page, page in enumerate(page_list):
                if count > 1 and index_page > 0:
                    folder = self.folder_visualcontainers(page_list[0]["name"])
                else:
                    folder = self.folder_visualcontainers(navigation_page_display_name)

                # 重写 config.json
                nbt = f"9{str(index_page).zfill(4)}_{page['name']}"
                target = os.path.join(folder, nbt)
                # 如果有文件夹,先删除掉
                if os.path.exists(target):
                    shutil.rmtree(target)
                # 删除带有关键字 btn_key_world 的按钮
                for visual_folder in os.listdir(folder):
                    if visual_folder.__contains__(btn_key_world):
                        shutil.rmtree(os.path.join(folder, visual_folder))
                # 复制源文件夹内容到目标文件夹
                shutil.copytree(source, target)

    def rewrite_navigation_button_json(self):
        """重写导航按钮的配置

        :return: None
        :rtype: None
        """
        report_page = self.read_report_page_json()
        navigation_page_display_name = report_page["pages"][1]["displayName"]
        content_page = report_page["name_group"]
        count = len(content_page)
        for index_page_list, page_list in enumerate(content_page):
            for index_page, page in enumerate(page_list):
                if count > 1 and index_page > 0:  # 分类导航
                    folder = self.folder_visualcontainers(page_list[0]["name"])
                    index = index_page - 1
                elif count > 1 and index_page == 0:  # 总导航
                    folder = self.folder_visualcontainers(navigation_page_display_name)
                    index = index_page_list
                else:  # 总导航
                    folder = self.folder_visualcontainers(navigation_page_display_name)
                    index = index_page

                # 以下几个属性公用
                page_count = len(page_list)
                nbt = f"9{str(index_page).zfill(4)}_{page['name']}"
                target = os.path.join(folder, nbt)
                visualcontainer_json = os.path.join(target, "visualContainer.json")
                config_json = os.path.join(target, "config.json")
                ordinal = page["ordinal"]
                displayname = page["name"]
                self.rewrite_visualcontainer_json(page_count, index, visualcontainer_json, config_json, ordinal, displayname)

    pbi_tools_command_compile_status = True  # 默认状态是正从的

    def pbi_tools_command_compile(self):
        """生成 PBIT 覆盖设置为 true

        :return: 执行命令行后的输出
        :rtype: str
        """
        # command 命令
        pbi_tools_command_compile = f'"{self.exe_pbi_tools}" compile -folder "{self.folder_temp}" -format PBIT -outPath "{self.path_pbit_target}" -overwrite true'

        # 执行编译合成命令
        # os.system(pbi_tools_command_compile)
        # 静默运行,不显示 cmd 的黑框
        # run(pbi_tools_command_compile, shell=True)
        text_out = execCmd(pbi_tools_command_compile)

        if not os.path.exists(self.path_pbit_target):  # 文件不存在，及未编译成功
            self.pbi_tools_command_compile_status = False

        # 判断是否存在,完成后删除 temp 文件夹
        if os.path.exists(self.folder_temp):
            shutil.rmtree(self.folder_temp)
        return text_out

    def delete_folder_pbix_extract(self, folder_pbix_extract_x: int = None):
        """判断是否存在, 完成后删除 pbix_extract 文件夹

        :param folder_pbix_extract_x: 第几个提取的文件, 默认是空
        :type folder_pbix_extract_x: int
        :return: None
        :rtype: None
        """

        if os.path.exists(self.folder_pbix_extract(folder_pbix_extract_x)):
            shutil.rmtree(self.folder_pbix_extract(folder_pbix_extract_x))

    def extract_is_match_config_json(self, folder_pbix_extract_x=None) -> bool:
        """判断模板是否匹配 config_json

        主要是观察表和列是否一致

        Args:
            folder_pbix_extract_x (int): 第几个提取的文件,默认是空

        Returns:bool
        """
        tc_extract = self.get_table_columns(folder_pbix_extract_x)
        dic = read_json(self.config_json_path)
        return tc_extract == dic["TableColumns"]