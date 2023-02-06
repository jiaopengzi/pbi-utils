# -*- encoding: utf-8 -*-
"""
@File    :   methods.py
@Time    :   2022/11/10 16:23
@Author  :   焦棚子
@Email   :   jiaopengzi@qq.com
@Blog    :   https://jiaopengzi.com/
@Version :   1.0.0"
"""
import json
import os
import shutil
import sys


def restart_window():
    """重启程序

    Returns:None

    """
    python = sys.executable
    os.execl(python, python, *sys.argv)


def remove_file_list(root: str, file_name_list: list) -> list:
    """删除给定的文件列表, 文件名称是相对 root 根目录的

    :param root:根目录
    :type root:str
    :param file_name_list:相对路径的文件名称列表
    :type file_name_list:list
    :return:返回删除后的绝对路径列表
    :rtype:list
    """
    if root and file_name_list:
        path_list = []
        for file in file_name_list:
            file_path = os.path.join(root, file)
            if os.path.isfile(file_path) and os.path.exists(file_path):
                path_list.append(file_path)
                os.remove(file_path)
        return path_list


def remove_tree_list(root: str, folder_list: list) -> list:
    """删除给定的文件夹列表, 文件夹名称是相对 root 根目录的

    :param root:根目录
    :type root:str
    :param folder_list:相对路径的文件夹名称列表
    :type folder_list:list
    :return:返回删除后的绝对路径列表
    :rtype:list
    """
    if root and folder_list:
        path_list = []
        for folder in folder_list:
            folder_path = os.path.join(root, folder)
            if os.path.isdir(folder_path) and os.path.exists(folder_path):
                path_list.append(folder_path)
                shutil.rmtree(folder_path)
        return path_list


def read_file_to_str(path: str) -> str:
    """读取文本文件

    Args:
        path (str):文本文件的路径

    Returns:读取的文件的文本内容

    """
    with open(path, "r", encoding="utf8") as f:
        return f.read()


def read_json(path: str) -> dict:
    """读取 json 文件

    Args:
        path (str):json文件的路径

    Returns:json 转成的字典

    """
    with open(path, "r", encoding="utf8") as f:
        return json.load(f, strict=False)


def write_str_in_file(path: str, text_str: str, encoding="utf8") -> None:
    """覆盖写入文本字符串写入文件

    Args:
        path (str):
        text_str (str): 需要写入文本
        encoding (str): 编码方式，默认为 encoding="utf8"

    Returns:None

    """
    with open(path, "w", encoding=encoding) as f:
        f.write(text_str)


def write_json_in_file(path: str, json_dic: dict) -> None:
    """覆盖写入 json 文件

    Args:
        path (str):json文件的路径
        json_dic (dict):需要写入的字典内容

    Returns:None

    """
    with open(path, "w", encoding="utf8") as f:
        f.write(json.dumps(json_dic, indent=4, ensure_ascii=False))


def create_folder(folder):
    """判断是否存在, 不存在就新建, 存在的话, 则pass

    :param folder: 需要创建的文件夹绝对路径
    :type folder: str
    :return: None
    :rtype: None
    """

    if not os.path.exists(folder):  # 判断是否存在,如果不存在则创建目录
        os.makedirs(folder)


def init_folder(folder):
    """初始化文件夹路径，有则删除后新建，无则新建，保证是空文件夹

    :param folder: 需要创建的文件夹绝对路径
    :type folder: str
    :return: None
    :rtype: None
    """

    create_folder(folder)
    if os.path.getsize(folder):  # 判断是否为空,不为空则删除后新建
        shutil.rmtree(folder)
        os.makedirs(folder)


def search_fuzzy(search_list, keyword) -> list:
    """list 模糊查询

    Args:
        search_list (list):需要被查找的数据源
        keyword (str):匹配的关键字

    Returns:
        返回搜索到的结果 list
    """
    if keyword:
        result_list = [item for item in search_list if keyword in item]
        return result_list or ["nothing"]
    return search_list


def distinct_list_text(text_str, delimiter, is_int=False) -> list:
    """文本 list 去重去首位空格

    :param text_str: 文本字符串
    :type text_str: str
    :param delimiter: 分隔符
    :type delimiter: str
    :param is_int: 是否是整数类型, 默认 False
    :type is_int: bool
    :return: list_new
    :rtype: list
    """

    text_str = text_str.strip()
    list_old = text_str.split(delimiter)
    list_new = []
    for item in list_old:
        if item not in list_new and item != "":
            item = item.strip()
            if is_int:
                item = int(item)
            list_new.append(item)
    return list_new


def execCmd(cmd):
    """ 运行 cmd 命令

    :param cmd: cmd 命令
    :type cmd: str
    :return: cmd 命令后的输出
    :rtype: str
    """
    r = os.popen(cmd)
    text_out = r.read()
    r.close()
    return text_out