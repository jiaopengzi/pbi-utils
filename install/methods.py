# -*- encoding: utf-8 -*-
"""
@File    :   methods.py
@Time    :   2022/11/10 16:23
@Author  :   焦棚子
@Email   :   jiaopengzi@qq.com
@Blog    :   https://jiaopengzi.com/
@Version :   1.0.0"
"""
import os
import shutil
import zipfile
from string import Template

import pyinstaller_versionfile


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


def create_folder(folder):
    """判断是否存在, 不存在就新建, 存在的话, 则pass

    :param folder: 需要创建的文件夹绝对路径
    :type folder: str
    :return: None
    :rtype: None
    """

    if not os.path.exists(folder):  # 判断是否存在,如果不存在则创建目录
        os.makedirs(folder)


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


def create_version_rc_pyinstaller(file_rc_path: str, _version: str, _company_name: str, _file_description: str,
                                  _internal_name: str, _legal_copyright: str, _original_filename: str, _product_name: str,
                                  _translations: list = None) -> None:
    """生成 pyinstaller 的 version.rc 文件

    参考：
    https://stackoverflow.com/questions/14624245/what-does-a-version-file-look-like
    https://learn.microsoft.com/zh-cn/windows/win32/menurc/versioninfo-resource
    https://learn.microsoft.com/zh-cn/windows/win32/api/verrsrc/ns-verrsrc-vs_fixedfileinfo?redirectedfrom=MSDN
    https://learn.microsoft.com/zh-cn/windows/win32/menurc/icon-resource?redirectedfrom=MSDN
    :param file_rc_path:文件路径
    :type file_rc_path:str
    :param _version:版本信息,文件版本和产品版本一致
    :type _version:str
    :param _company_name:公司名称
    :type _company_name:str
    :param _file_description:文件描述
    :type _file_description:str
    :param _internal_name:内部名称
    :type _internal_name:str
    :param _legal_copyright:版权信息
    :type _legal_copyright:str
    :param _original_filename:原文件名称
    :type _original_filename:str
    :param _product_name:产品名称
    :type _product_name:str
    :param _translations:翻译和字符集,默认为 [0,1200] 中性和utf8
    :type _translations:list
    :return:None
    :rtype:None
    """

    if _translations is None:
        _translations = [0, 1200]
    # 自定义创建版本文件
    pyinstaller_versionfile.create_versionfile(
            output_file=file_rc_path,
            version=_version,
            company_name=_company_name,
            file_description=_file_description,
            internal_name=_internal_name,
            legal_copyright=_legal_copyright,
            original_filename=_original_filename,
            product_name=_product_name,
            translations=_translations  # 中性,字符集utf8
    )


def generate_add_del_nsi_str(pbi_utils_folder_source: str, del_folder_list: list = None, del_file_list: list = None) -> dict:
    """ 在 .nsi 中文件生成安装和卸载信息

    :param pbi_utils_folder_source: 需要打包的文件夹路径 r"C:\desktop\power-bi-custom-template-dev\dist\pbi-utils"
    :type pbi_utils_folder_source: str
    :param del_folder_list: 需要删除的文件夹列表
    :type del_folder_list: list
    :param del_file_list: 需要删除的文件列表
    :type del_file_list: list
    :return:安装信息和卸载信息字典 {"add_str": add_str, "del_str": del_str}
    :rtype:dict
    """

    # 1、删除文件夹
    if del_folder_list:
        remove_tree_list(pbi_utils_folder_source, del_folder_list)

    # 2、删除文件
    if del_file_list:
        remove_file_list(pbi_utils_folder_source, del_file_list)

    # 3、生成 安装和卸载的 nsi 脚本信息
    inst_dir_prefix = "SetOutPath $INSTDIR"  # 前缀
    del_file_prefix = "Delete $INSTDIR"  # 前缀
    tree_add = []  # 增加文件的路径列表
    dirs_del = []  # 需要删除的文件夹路径-无序列表
    files_del = []  # 需要删除的文件路径列表
    level_max = 0  # 需要删除的文件夹路径最大深度
    # 通过 os.walk 历遍, 文件夹下面的子文件夹和文件信息
    for root, _, files in os.walk(pbi_utils_folder_source):
        inst_dir = root.replace(pbi_utils_folder_source, inst_dir_prefix)  # 需要添加的文件夹路径
        tree_add.append(inst_dir)
        level = list(inst_dir).count("\\")  # 判断文件夹路径层数
        level_max = max(level_max, level)  # 动态获取文件夹最大层数
        del_dir = inst_dir.replace("SetOutPath", "RMDir")  # 替换生成删除文件夹命令
        dirs_del.append({"path": del_dir, "level": level})  # 无序文件夹路径
        for file in files:
            path = os.path.join(root, file)  # 文件路径
            file_add = f'File {path}'  # 拼接增加的文件命令
            tree_add.append(file_add)
            file_del = path.replace(pbi_utils_folder_source, del_file_prefix)  # 需要添加的文件夹路径
            files_del.append(file_del)

    # 需要删除的文件夹路径-有序列表，从层级最深开始
    dirs_del_level = []
    for level in reversed(range(level_max + 1)):  # 用倒序来排序层级最深的文件夹
        dirs_del_level.extend(dir_del["path"] for dir_del in dirs_del if dir_del["level"] == level)

    files_del.extend(dirs_del_level)

    return {"add_str": "\n".join(tree_add), "del_str": "\n".join(files_del)}


def create_install_nsi_run(file_nsi_target: str, src_install_folder: str, version: str, company: str,
                           description: str, original_file_name: str, name_product: str, install_folder: str,
                           main_exe: str, product_web_site: str, branding_text: str, icon: str,
                           un_icon: str, welcome_bmp: str, un_welcome_bmp: str, license_txt: str,
                           out_file: str, nsi_template: str,
                           makensis_exe: str = None, compressor: str = "lzma", encoding: str = "GB2312",
                           del_folder_list: list = None, del_file_list: list = None) -> str:
    """ 创建 nsi 打包脚本文件, 并自动执行打包

    :param file_nsi_target:生成的 nsi 脚本文件路径
    :type file_nsi_target:str
    :param src_install_folder:需要安装的文件夹路径
    :type src_install_folder:str
    :param version:版本号，必须是按照 32 位写法, 如：1.0.0.0
    :type version:str
    :param company:公司和发布者名称
    :type company:str
    :param description:打包文件描述文字
    :type description:str
    :param original_file_name:打包文件原名称
    :type original_file_name:str
    :param name_product:产品名称
    :type name_product:str
    :param install_folder:安装到用户电脑的根目录文件夹名称
    :type install_folder:str
    :param main_exe:主文件的名称
    :type main_exe:str
    :param product_web_site:产品的 url
    :type product_web_site:str
    :param branding_text:品牌文本
    :type branding_text:str
    :param icon:安装时候的 ico
    :type icon:str
    :param un_icon: 卸载时候的 ico
    :type un_icon:str
    :param welcome_bmp:安装页面的主图 bmp 格式 w164 * h314
    :type welcome_bmp:str
    :param un_welcome_bmp:卸载页面的主图 bmp 格式 w164 * h314
    :type un_welcome_bmp:str
    :param license_txt:授权文件的路径，需要是 txt 格式文件
    :type license_txt:str
    :param out_file:生成的安装包文件路径
    :type out_file:str
    :param nsi_template: nsi 模板文件
    :type nsi_template:str
    :param makensis_exe: makensis.exe路径,添加了环境变量就可以不写，默认None
    :type makensis_exe:str
    :param compressor:压缩方式，默认 lzma 可选 zlib bzip2
    :type compressor:str
    :param encoding: nsi 文件编码方式，默认是 GB2312
    :type encoding:str
    :param del_folder_list: 需要删除的文件夹列表
    :type del_folder_list: list
    :param del_file_list: 需要删除的文件列表
    :type del_file_list: list
    :return:None
    :rtype:None
    """

    add_del = generate_add_del_nsi_str(src_install_folder, del_folder_list, del_file_list)
    add_str = add_del["add_str"]  # 增加的信息
    del_str = add_del["del_str"]  # 卸载删除的信息
    # 模板字典匹配
    nis_dict = {
            "_PRODUCT_NAME"                  : name_product,
            "_MAIN_EXE"                      : main_exe,
            "_INSTALL_FOLDER"                : install_folder,
            "_PRODUCT_VERSION"               : version,
            "_PRODUCT_PUBLISHER"             : company,
            "_PRODUCT_WEB_SITE"              : product_web_site,
            "_SetCompressor"                 : compressor,
            "_MUI_ICON"                      : icon,
            "_MUI_UNICON"                    : un_icon,
            "_MUI_WELCOMEFINISHPAGE_BITMAP"  : welcome_bmp,
            "_MUI_UNWELCOMEFINISHPAGE_BITMAP": un_welcome_bmp,
            "_MUI_PAGE_LICENSE"              : license_txt,
            "_OutFile"                       : out_file,
            # 以下是安装包的元数据
            "_VIProductVersion"              : version,
            "_VIFileVersion"                 : version,
            "_CompanyName"                   : company,
            "_FileDescription"               : description,
            "_ProductVersion"                : version,
            "_LegalCopyright"                : f"${{U+00A9}} {company}. All rights reserved.",
            "_OriginalFilename"              : original_file_name,
            "_ProductName"                   : f"{name_product}-setup",
            "_BrandingText"                  : branding_text,
            "_add_str"                       : add_str,
            "_del_str"                       : del_str,
    }

    template = Template(nsi_template)
    nsi = template.substitute(nis_dict)
    write_str_in_file(file_nsi_target, nsi, encoding=encoding)

    return execCmd(f'"{makensis_exe}" {file_nsi_target}') if makensis_exe else execCmd(f"makensis {file_nsi_target}")


def zip_folder(zip_folder_src: str, out_zip_file: str, has_root_dir: bool = False) -> None:
    """ zip 压缩文件夹

    :param zip_folder_src: 需要压缩的文件夹路径
    :type zip_folder_src: str
    :param out_zip_file: 压缩后 zip 文件路径
    :type out_zip_file: str
    :param has_root_dir: 是否包含根目录文件夹, 默认 False
    :type has_root_dir: bool
    :return:None
    :rtype:None
    """
    with zipfile.ZipFile(out_zip_file, "w", zipfile.ZIP_LZMA) as f:  # zip文件
        for root, dirs, files in os.walk(zip_folder_src):  # os.walk 历遍文件
            dir_name = root.replace(zip_folder_src, "")  # 目录的相对路径
            if has_root_dir:  # 是否保留根目录
                dir_name = root.replace(os.path.dirname(zip_folder_src), "")
            for file in files:
                f.write(os.path.join(root, file), os.path.join(dir_name, file))