# -*- encoding: utf-8 -*-
"""
@File           :   create_install_file.py
@Time           :   2022-11-17, 周四, 16:1
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   生成用户文件 zip 和 setup.exe
"""

import os
import shutil
import zipfile

from config import BASE_DIR, VERSION_INFO
from install.install_nsi_template import INSTALL_NSI_TEMPLATE
from install.methods import create_folder, create_install_nsi_run, create_version_rc_pyinstaller, execCmd, zip_folder

# 输出版文文件夹
release_folder = r"C:\desktop\pbi-utils-release"  # 用户版本文件夹
create_folder(release_folder)
# 1、 .rc 版本文件信息
file_rc = os.path.join(BASE_DIR, "version.rc")  # .rc 文件夹路径
version = VERSION_INFO["version"]  # 版本信息
company_name = "jiaopengzi"  # 公司信息或者叫发布者
file_description = "pbi-utils main program"  # 主执行文件的描述
internal_name = "pbi-utils"  # 内部名称
legal_copyright = "© jiaopengzi. All rights reserved."  # 主执行文件的安全信息
original_filename = "pbi-utils.exe"  # 原来名字，一般和发布名称相同
product_name = "pbi-utils"  # 发布名称不带 .exe
dist = os.path.join(BASE_DIR, "dist")  # 生成的用户文件夹
if os.path.exists(dist):
    shutil.rmtree(dist)  # 删除原来 pyinstaller 打包文件

# 生成 .rc 元数据文件
create_version_rc_pyinstaller(file_rc, version, company_name, file_description,
                              internal_name, legal_copyright, original_filename, product_name)
print("==================================================================")
print(f".rc版本文件创建完毕：{file_rc}")

# 2、pyinstaller 生成 可执行文件
execCmd("pyinstaller main_single_exe.spec")  # 单个文件模式
execCmd("pyinstaller main_folder.spec")  # 文件夹模式

# 3、nsi 文件生成使用打包工具打包
file_nsi_target = os.path.join(BASE_DIR, "setup.nsi")  # .nsi 文件路径
src_install_folder = os.path.join(BASE_DIR, "dist\pbi-utils")  # 需要打成安装包的文件夹
description = "about pbi-utils setup"  # 安装包文件的描述
original_file_name = f"{product_name}-setup.exe"  # 安装包原来的名称
install_folder = "pbi-utils"  # 安装到用户的电脑上的根目录文件夹
main_exe = "pbi-utils.exe"  # 主执行文件名称
product_web_site = "https://jiaopengzi.com/2880.html"  # 产品的 url
branding_text = "www.jiaopengzi.com"  # 品牌文字
icon = os.path.join(BASE_DIR, "favicon.ico")  # 安装时的 logo
un_icon = os.path.join(BASE_DIR, "favicon.ico")  # 卸载时的 logo
welcome_bmp = os.path.join(BASE_DIR, "image\logo_w164_h314.bmp")  # 安装欢迎页面的主图
un_welcome_bmp = os.path.join(BASE_DIR, "image\logo_w164_h314.bmp")  # 卸载页面的主图

# 授权文件夹
license_txt = os.path.join(BASE_DIR, "LICENSE.txt")  # 授权文字的 txt 路径
shutil.copy(license_txt, src_install_folder)  # 复制授权文件到 dist 文件夹

out_file_install = os.path.join(release_folder, f"{product_name}-{version}-setup.exe")  # 输出的 安装包文件夹路径
makensis_exe = r"C:\Program Files (x86)\NSIS\makensis.exe"  # nsi 执行的程序路径,需要自行安装
# 打成安装包前需要删除的多余文件夹
del_folder_list = ["markupsafe", "yaml", "PySide6\\translations", "PySide6\\plugins\\imageformats",
                   "PySide6\\plugins\\networkinformation", "PySide6\\plugins\\platforminputcontexts", "PySide6\\plugins\\tls"]
# 打成安装包前需要删除的多余文件
del_file_list = ["PySide6\\opengl32sw.dll", "PySide6\\Qt6Network.dll", "PySide6\\Qt6OpenGL.dll", "PySide6\\Qt6Qml.dll", "PySide6\\Qt6QmlModels.dll",
                 "PySide6\\Qt6Quick.dll", "PySide6\\Qt6VirtualKeyboard.dll", "PySide6\\QtNetwork.pyd"]
# del_folder_list = None
# del_file_list = None
# 删除原来的安装包文件
if os.path.exists(out_file_install):
    os.remove(out_file_install)
# 生成打包文件 nsi 和自动打包
out_cmd = create_install_nsi_run(file_nsi_target, src_install_folder, version, company_name,
                                 description, original_file_name, internal_name, install_folder,
                                 main_exe, product_web_site, branding_text, icon,
                                 un_icon, welcome_bmp, un_welcome_bmp, license_txt,
                                 out_file_install, INSTALL_NSI_TEMPLATE, makensis_exe,
                                 del_folder_list=del_folder_list, del_file_list=del_file_list)

# 输出打包信息查看是否有错
print("==================================================================")
print(out_cmd)

# 生成用户 zip 文件
out_file_zip = os.path.join(release_folder, f"{product_name}-{version}-portable.zip")
if os.path.exists(out_file_zip):
    os.remove(out_file_zip)
zip_folder(src_install_folder, out_file_zip, has_root_dir=True)

# # exe文件打包
# out_file_setup_zip = f'{out_file_install[:-4]}.zip'  # 输出的 安装包文件夹路径
# with zipfile.ZipFile(out_file_setup_zip, "w", zipfile.ZIP_LZMA) as f:  # zip文件
#     file_in_zip = out_file_install.replace(release_folder, "")
#     f.write(out_file_install, file_in_zip)
#
# os.remove(out_file_install)

print("==================================================================")
print(f"zip安装包完成：{file_rc}")