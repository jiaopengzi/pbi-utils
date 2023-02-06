# -*- encoding: utf-8 -*-
"""
@File           :   ui_00_about.py
@Time           :   2022-11-10, 周四, 16:56
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   ui 关于
"""

from PySide6.QtWidgets import QComboBox, QHBoxLayout, QTextBrowser, QVBoxLayout, QWidget

from config import ABOUT_HTML
from configfiles import DISPLAY_CONFIG, EN_US, Language, ZH_CN, lang_display
from ui.method import UiMethod
from utils.methods import restart_window


class About(UiMethod):
    """关于页面的ui
    """
    about = "about"
    Language_list = [lang_display[lang] for lang in lang_display]

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)

    def ui_about(self):
        """ui 关于

        :return:返回中间页面
        :rtype:QWidget
        """

        self.about_html = ABOUT_HTML
        self.about_dic = DISPLAY_CONFIG[self.about]
        for key in self.about_dic:
            setattr(self, key, key)

        self.widget_about = QWidget()
        vbox = QVBoxLayout()
        hbox = QHBoxLayout()
        tb_about = QTextBrowser()
        # tb_about.resize(self.centralWidget().size())
        tb_about.setHtml(self.about_html)
        tb_about.setOpenExternalLinks(True)
        # vbox.addStretch()
        vbox.addWidget(tb_about)
        # vbox.addStretch()

        language = Language()
        current_text = lang_display[language.read_text_language()]

        combo_layout = self.combobox_layout_x(self.about,
                                              self.QLabel_language,
                                              self.QComboBox_language,
                                              self.combo_currentTextChanged_about,
                                              minimum_width=100,
                                              item_list=self.Language_list,
                                              current_text=current_text)

        vbox.addLayout(combo_layout)
        hbox.addStretch()
        hbox.addLayout(vbox)
        hbox.addStretch()
        self.widget_about.setLayout(hbox)
        self.progress_bar_display()

        return self.widget_about

    def combo_currentTextChanged_about(self):
        """语言下拉框变化后重启

        :return:None
        :rtype:None
        """
        combo = self.findChild(QComboBox, f"{self.about}_{self.QComboBox_language}")
        self.set_text_color(combo)
        lang_new_display = combo.currentText()
        language = Language()
        if lang_new_display == lang_display[ZH_CN]:
            self.language_handoff(language, ZH_CN)
        elif lang_new_display == lang_display[EN_US]:
            self.language_handoff(language, EN_US)

    def language_handoff(self, language: object, lang_str: str) -> None:
        """语言切换后操作

        :param language:语言类实例
        :param lang_str:具体语言
        :return:None
        """
        language.write_lng_text_in_file(lang_str)
        self.msg_display("msg1201", is_info=True)
        restart_window()