# -*- encoding: utf-8 -*-
"""
@File           :   validators.py
@Time           :   2022-11-10, 周四, 21:33
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   校验器
"""

import os
import re

from configfiles import MSG


class Validator(object):
    """Validator 文本校验
    """

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    @staticmethod
    def is_blank(input_):
        """判断是否为空字符串

        Args:
            input_ (str): 键入字符串
        Returns:
            bool: 返回bool
        """
        return not len(input_.strip())

    @staticmethod
    def is_contain(input_, keyword):
        """判断是包含以为关键字结尾,即后缀名。

        Args:
            input_ (str): 键入字符串
            keyword (str): 键入字符串

        Returns:
            bool: 返回bool
        """
        return bool(input_.lower().endswith(keyword))

    @staticmethod
    def has_space(input_):
        """判断是否包含空格

        Args:
            input_ (str): 键入字符串

        Returns:
            bool: 返回bool
        """
        return bool(re.search("\s", input_))

    def is_pbix(self, input_):
        """判断是否是 pbix 文件,保证 pbix 存在.

        Args:
            input_ (str): 键入字符串

        Returns:
            tuple: 元组(True, "正确") 或者 (False, "请输入正确的 pbix 文件")
        """
        if self.is_blank(input_):
            return False, MSG["msg1101"]["msg"]
        if self.has_space(input_):
            return False, MSG["msg1102"]["msg"]
        if not os.path.exists(input_):
            return False, MSG["msg1107"]["msg"]
        return (True, "正确") if self.is_contain(input_, ".pbix") and os.path.isfile(input_) else (False, MSG["msg1103"]["msg"])

    def is_text(self, input_):
        """文本不为空，在 ui 中已经验证了输入。

        Args:
            input_ (str): 键入字符串

        Returns:
            tuple: 元组(True, "正确") 或者 (False, "不能为空！")
        """
        return (False, MSG["msg1101"]["msg"]) if self.is_blank(input_) else (True, "正确")

    def is_pbit(self, input_, is_create=False):
        """判断是否是 pbit 文件

        Args:
            input_ (str): 键入字符串
            is_create (bool): 是否为新建写入，默认 False

        Returns:
            tuple: 元组(True, "正确") 或者 (False, "请输入正确的 pbit 文件")
        """
        if self.is_blank(input_):
            return False, MSG["msg1101"]["msg"]
        if self.has_space(input_):
            return False, MSG["msg1102"]["msg"]
        if not is_create and not os.path.exists(input_):
            return False, MSG["msg1107"]["msg"]
        return (True, "正确") if self.is_contain(input_, ".pbit") else (False, MSG["msg1104"]["msg"])

    def is_json(self, input_, is_create=False):
        """判断是否是 json 文件

        Args:
            input_ (str): 键入字符串
            is_create (bool): 是否为新建写入，默认 False

        Returns:
            tuple: 元组(True, "正确") 或者 (False, "请输入正确的 json 文件")
        """
        if self.is_blank(input_):
            return False, MSG["msg1101"]["msg"]
        if self.has_space(input_):
            return False, MSG["msg1102"]["msg"]
        if not is_create and not os.path.exists(input_):
            return False, MSG["msg1107"]["msg"]
        return (True, "正确") if self.is_contain(input_, ".json") else (False, MSG["msg1105"]["msg"])

    def is_dir(self, input_):
        """判断是否是文件夹

        Args:
            input_ (str): 键入字符串

        Returns:
            tuple: 元组(True, "正确") 或者 (False, "请输入正确的文件夹路径")
        """
        if self.is_blank(input_):
            return False, MSG["msg1101"]["msg"]
        if self.has_space(input_):
            return False, MSG["msg1102"]["msg"]
        if not os.path.exists(input_):
            return False, MSG["msg1107"]["msg"]
        return (True, "正确") if os.path.isdir(input_) else (False, MSG["msg1106"]["msg"])


# if __name__ == '__main__':
#     vd = Validator()
#     print(vd.is_pbix("C:/desktop/power-bi-custom-template-dev/demo/template_1.42 .pbix"))