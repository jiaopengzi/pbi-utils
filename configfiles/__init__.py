# -*- encoding: utf-8 -*-
"""
@File           :   __init__.py
@Time           :   2022-11-10, 周四, 21:36
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   init
"""

import os

from configfiles.qss_text import QSS
from utils.methods import create_folder

APP_DATA = os.environ['AppData']
APP_DATA_PBI_UTILS = os.path.join(APP_DATA, "pbi-utils")

ZH_CN = "zh-cn"
EN_US = "en-us"
lang_display = {
        ZH_CN: "简体中文",
        EN_US: "English"
}

file_pbi_utils_language = os.path.join(APP_DATA_PBI_UTILS, "language.txt")


class Language(object):
    """语言类

    Args:
        object (object): 默认
    """
    folder_pbi_utils_language = APP_DATA_PBI_UTILS
    file_pbi_utils_language = file_pbi_utils_language

    def write_lng_text_in_file(self, lang_text: str) -> None:
        """写入语言

        Args:
            lang_text (str): 语言模式，目前支持中文和英文 "zh-cn" "en-us"
        """
        """写入语言

        Args:
            lang_text (str): 语言模式，目前支持中文和英文 "zh-cn" "en-us"
        """
        create_folder(self.folder_pbi_utils_language)
        with open(self.file_pbi_utils_language, "w", encoding="utf8") as f:
            f.write(lang_text)

    def read_text_language(self) -> str:
        """读取语言信息

        Returns:
             返回语言的字符串，目前支持中文和英文 "zh-cn" "en-us"
        """

        if os.path.exists(self.file_pbi_utils_language):
            with open(self.file_pbi_utils_language, "r", encoding="utf8") as f:
                lang = f.read()
                if lang in [ZH_CN, EN_US]:
                    return lang
        return ZH_CN


# 执行导入
if not os.path.exists(file_pbi_utils_language):
    language_text = ZH_CN
else:
    config = Language()
    language_text = config.read_text_language()

# 1
if language_text == ZH_CN:
    from configfiles.menu_action_cn import MENU_ACTION_LIST
    from configfiles.display_config_cn import DISPLAY_CONFIG
    from configfiles.about_html_cn import ABOUT_HTML
    from configfiles.msg_config_cn import MSG
    from configfiles.report_visual_template_base_cn import REPORT_VISUAL_TEMPLATES_BASE
    from configfiles.report_base_cn import REPORT_PAGE_BASE_JSON
    from configfiles.dax_cn import RLS_DAX_TEMPLATE, TEMPLATE_DAX_LIST, DAX_REPORT_VISUAL_TEMPLATE
    from configfiles.column_value_cn import (REPORT_VISUAL_COLUMN_NAME,
                                             DATA_CATEGORY, COLUMN_NAME,
                                             DISPLAYOPTON,
                                             VERTICAL_ALIGNMENT, VISIBILITY,
                                             PERMISSION_INIT_COLUMN_NAME,
                                             PERMISSION_EDIT_COLUMN_NAME,
                                             PERMISSION_EDIT_COLUMN_NAME_ROLE)

elif language_text == EN_US:
    from configfiles.menu_action_en import MENU_ACTION_LIST
    from configfiles.display_config_en import DISPLAY_CONFIG
    from configfiles.about_html_en import ABOUT_HTML
    from configfiles.msg_config_en import MSG
    from configfiles.report_visual_template_base_en import REPORT_VISUAL_TEMPLATES_BASE
    from configfiles.report_base_en import REPORT_PAGE_BASE_JSON
    from configfiles.dax_en import RLS_DAX_TEMPLATE, TEMPLATE_DAX_LIST, DAX_REPORT_VISUAL_TEMPLATE
    from configfiles.column_value_en import (REPORT_VISUAL_COLUMN_NAME,
                                             DATA_CATEGORY, COLUMN_NAME,
                                             DISPLAYOPTON,
                                             VERTICAL_ALIGNMENT, VISIBILITY,
                                             PERMISSION_INIT_COLUMN_NAME,
                                             PERMISSION_EDIT_COLUMN_NAME,
                                             PERMISSION_EDIT_COLUMN_NAME_ROLE)