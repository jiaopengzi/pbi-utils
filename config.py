# -*- encoding: utf-8 -*-
"""
@File           :   config.py
@Time           :   2022-11-10, 周四, 21:45
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   项目配置
"""

import contextlib
import json
import os
import sys
from string import Template

from configfiles import ABOUT_HTML, QSS, language_text


# temp 临时文件夹,获取用户的临时文件夹 os.getenv("TEMP")

def python_file_path() -> str:
    """获取 python 程序路径

    Returns:
            返回文件绝对路径
    """
    if getattr(sys, "frozen", False):
        return os.path.abspath(sys.executable)
    return os.path.abspath(__file__)


VERSION_INFO = {"version"     : "1.0.1.0",
                "release_date": "2023-03-28"}

temp = Template(ABOUT_HTML)
ABOUT_HTML = temp.substitute(VERSION_INFO)
# 项目文件的绝对路径
BASE_DIR = os.path.dirname(python_file_path())
APP_DATA = os.environ['AppData']
INSTALL_DIR = "pbi-utils"


def runtime_tmpdir() -> str:
    """项目展开运行的临时文件夹路径

    Returns:
            项目展开运行的临时文件夹路径
    """
    return getattr(sys, '_MEIPASS', os.path.dirname(python_file_path()))


def resource_path(relative_path) -> str:
    """获取资源路径,在打包运行时能找到对应的文件夹路径

    Args:
        relative_path (str):项目文件夹的相对路径

    Returns:
            获取运行时候的资源绝对路径（临时文件夹中）
    """
    return os.path.join(runtime_tmpdir(), relative_path)


# def qss():
#     # qss 样式
#     path = os.path.join(BASE_DIR, "qss\\style1.qss")
#     with open(path, "r", encoding="utf-8") as f:
#         return f.read()

def qss() -> str:
    """qss长文本格式

    Returns:
        qss长文本格式

    """
    return QSS


def read_json(path_json: str) -> dict:
    """读取 json 转成 字典

    Args:
        path_json (str): json 文件路径

    Returns: json 转换的字典

    """
    with open(path_json, "r", encoding="utf8") as f:
        return json.load(f, strict=False)


def is_custom_report_visual_templates_base() -> str:
    """判断是否存在自定义的 report_visual_templates_base.json

    Returns:report_visual_templates_base.json 路径
    """
    rvtb_path = os.path.join(APP_DATA, f"{INSTALL_DIR}\\custom\\report_visual_templates_base.json")
    if os.path.exists(rvtb_path):
        return rvtb_path


def is_custom_report_base() -> str:
    """判断是否存在自定义的 report_base.json

    Returns:report_base.json 路径
    """
    rb_path = os.path.join(APP_DATA, f"{INSTALL_DIR}\\custom\\report_base.json")
    if os.path.exists(rb_path):
        return rb_path


# 判断是否有符合要求的自定义数据
def set_custom_report_visual_templates_base(rvtb: dict) -> dict:
    """设置自定义的 report_visual_templates_base.json

    :param rvtb: report_visual_templates_base.json 默认字典
    :type rvtb: dict
    :return: rvtb
    :rtype: dict
    """
    if path_json := is_custom_report_visual_templates_base():
        dic = rvtb[0]
        with contextlib.suppress(Exception):
            custom = read_json(path_json)
            if dic.keys() == custom["ReportVisualTemplates"][0].keys():
                rvtb = custom["ReportVisualTemplates"]
                return rvtb


def set_custom_report_base(rpbj: dict) -> dict:
    """设置自定义的 report_base.json

    :param rpbj: report_base.json 默认字典
    :type rpbj: dict
    :return: rpbj
    :rtype: dict
    """
    if path_json := is_custom_report_base():
        dic = rpbj["ReportPages"][0]
        with contextlib.suppress(Exception):
            custom = read_json(path_json)
            if dic.keys() == custom["ReportPages"][0].keys():
                rpbj["ReportPages"] = custom["ReportPages"]
                return rpbj


# if __name__ == "__main__":
#     print(language_text)