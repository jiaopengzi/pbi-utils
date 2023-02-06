# -*- encoding: utf-8 -*-
"""
@File           :   ui_main.py
@Time           :   2022-11-10, 周四, 19:30
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   主窗体
"""

from PySide6.QtCore import QSize, Qt
from PySide6.QtGui import QAction, QIcon
from PySide6.QtWidgets import QLabel, QMainWindow, QMenu, QMenuBar, QProgressBar, QStatusBar, QToolBar

# 自定义模块
from config import qss
from configfiles import APP_DATA_PBI_UTILS, DISPLAY_CONFIG, MENU_ACTION_LIST
from ui.ui_00_about import About
from ui.ui_00_home import Home
from ui.ui_01_config_json_init import JsonInit
from ui.ui_02_report_visual_edit import ReportVisualEdit
from ui.ui_03_page_edit import PageEdit
from ui.ui_04_permission_init import PermissionInit
from ui.ui_05_permission_edit import PermissionEdit
from ui.ui_06_pbix2pbit import Pbix2Pbit
from ui.ui_07_pbixA2pbixB import PbixA2PbixB
from ui.ui_08_pbix2DAX import Pbix2DAX
from ui.ui_09_DAX2pbix import DAX2Pbix
from utils.methods import create_folder
from utils.pbit import Pbit
import resource


class UiMainWindow(QMainWindow, Home, About,
                   JsonInit, ReportVisualEdit, PageEdit,
                   PermissionInit, PermissionEdit,
                   Pbix2Pbit, PbixA2PbixB, Pbix2DAX, DAX2Pbix):
    """主窗体类，继承其它子窗体
    """

    ui_main = "ui_main"
    pbix_open_choose = None  # 选择的 pbix
    pbix_open_dic_list = None  # 当前打开的 pbix 明细

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.ui_main_dic = DISPLAY_CONFIG[self.ui_main]
        # 使用配置生成属性
        for key in self.ui_main_dic:
            setattr(self, key, key)
        create_folder(APP_DATA_PBI_UTILS)
        self.init_ui()
        self.show()

    def init_ui(self) -> None:
        """初始化窗体函数

        Returns:None
        """

        # 窗体尺寸
        self.resize(1000, 618)
        # 标题和 logo
        self.setWindowTitle("pbi-utils")
        icon = QIcon()
        icon.addFile(':/icon/image/logo.svg', QSize(), QIcon.Normal, QIcon.Off)
        # self.setWindowIcon(QIcon(resource_path("icons/logo.svg")))
        self.setWindowIcon(icon)

        # qss 样式
        self.setStyleSheet(qss())

        self.ui_menubar()
        self.ui_toolbar()
        self.ui_statusbar()
        self.manu_action_init()
        # 欢迎页
        self.setCentralWidget(self.ui_home())
        # self.setCentralWidget(self.ui_dax_2_pbix())

    # def radio_pbix_open_choose(self):
    #     dic_list = self.pbix_open_dic_list
    #     if dic_list is not None and len(dic_list) > 1:
    #         r_dic = {dic: dic for dic in dic_list}
    #         # TODO:中英文设置
    #         radio = Radio(display_dict=r_dic, window_title="请选择要连接的 pbix ")
    #         radio.exec()
    #         self.pbix_open_choose = radio.radio_result
    #         # print(self.pbix_open_choose)

    def pbix_open_choose_multi(self, line_edit: str) -> None:
        """待定，暂时不开发

        Args:
            line_edit (str): 键入字符串

        Returns:    None

        """
        # TODO: 待定，暂时不开发
        pass

    def one_pbix_online(self) -> None:
        """是否只有打开了一个 pbix 文件。将打开的 pbix 路径赋值给属性：self.pbix_open_choose

        Returns: None
        """
        pbit = Pbit()
        if info := pbit.pbi_tools_command_info():
            self.pbix_open_dic_list = info
            if len(info) == 1:
                for dic in info.values():
                    self.pbix_open_choose = dic["PbixPath"]

    def ui_menubar(self) -> None:
        """菜单栏

        Returns:    None

        """
        self.menubar = QMenuBar(self)
        self.menubar.setObjectName("menubar")
        self.setMenuBar(self.menubar)

    def ui_toolbar(self) -> None:
        """工具栏

        Returns:    None
        """
        self.toolBar = QToolBar()
        self.toolBar.setMinimumSize(QSize(200, 40))
        self.toolBar.setMovable(False)
        self.toolBar.setObjectName("toolBar")
        self.toolBar.setAllowedAreas(Qt.ToolBarArea.TopToolBarArea)  # 设置在顶部
        self.toolBar.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextUnderIcon)  # 图表文字样式
        self.addToolBar(Qt.ToolBarArea.TopToolBarArea, self.toolBar)

    def menu_x(self, objectname: str, displayname: str, ico: str, parent: object) -> None:
        """根据配置生成菜单栏

        Args:
            objectname (str): 对象名称
            displayname (str): 显示名称
            ico (str):  图片名称
            parent (str): 父级名称

        Returns:    None

        """
        # 图标
        icon = QIcon()
        # icon.addPixmap(QPixmap(resource_path(f':/icon/image/{ico}')), QIcon.Mode.Normal, QIcon.State.Off)
        icon.addFile(f':/icon/image/{ico}', QSize(), QIcon.Normal, QIcon.Off)
        menuX = QMenu(parent)
        menuX.setIcon(icon)
        menuX.setObjectName(objectname)
        menuX.setTitle(displayname)
        menuX.setFont(self.font_ui())
        # 添加动作
        parent.addAction(menuX.menuAction())

    def action_x(self, objectname: str, displayname: str, ico: str, parent: str, func_name: str) -> None:
        """根据配置创建动作

        Args:
            objectname (str): 对象名称
            displayname (str): 显示名称
            ico (str):  图片名称
            parent (str): 父级名称
            func_name (str):绑定对象的行数名称

        Returns:    None
        """
        actionX = QAction(self)
        icon = QIcon()
        icon.addFile(f':/icon/image/{ico}', QSize(), QIcon.Normal, QIcon.Off)
        actionX.setIcon(icon)
        actionX.setObjectName(objectname)  # action 名称
        actionX.setFont(self.font_ui())
        actionX.setText(displayname)
        actionX.triggered.connect(lambda: self.action_x_func(objectname, func_name))
        parent = self.findChild(QMenu, parent)
        parent.addAction(actionX)
        self.toolBar.addAction(actionX)

    def action_x_func(self, objectname: str, func_name: str) -> None:
        """根据每个 action 设置 CentralWidget 以及状态栏显示文字

        Args:
            objectname (str): 对象名称
            func_name (str):绑定对象的行数名称

        Returns:    None
        """
        menu_action = MENU_ACTION_LIST
        ui = getattr(self, func_name)
        self.setCentralWidget(ui())
        self.statusbar_label0.setText(menu_action[objectname]["displayname"])

    def manu_action_init(self) -> None:
        """菜单栏和对应动作初始化

        为了保证顺序的正确性，menu he action 要卸载一起

        Returns:    None
        """
        dic = MENU_ACTION_LIST
        for obj in dic:
            parent = self.menubar if dic[obj]["parent"] == "self" else self.findChild(QMenu, dic[obj]["parent"])
            if "menu_" in obj:
                self.menu_x(obj, dic[obj]["displayname"], dic[obj]["ico"], parent)
            else:
                self.action_x(obj, dic[obj]["displayname"], dic[obj]["ico"], dic[obj]["parent"], dic[obj]["func"])

    def ui_statusbar(self) -> None:
        """状态栏设置

        Returns:    None
        """

        menu_action = MENU_ACTION_LIST
        self.statusbar = QStatusBar()
        self.statusbar.setObjectName(self.QStatusBar_status)
        self.setStatusBar(self.statusbar)
        # 进度条
        self.statusbar_label0 = QLabel(menu_action["action_home"]["displayname"])
        self.statusbar_label0.setObjectName(f"{self.ui_main}_{self.QLabel_status}")
        self.statusbar_label0.setMinimumWidth(120)
        self.statusbar_progress_bar = QProgressBar()
        self.statusbar_progress_bar.setObjectName(f"{self.ui_main}_{self.QProgressBar_status}")
        self.statusbar_progress_bar.setFixedHeight(16)
        self.statusbar.addWidget(self.statusbar_label0)
        self.statusbar.addWidget(self.statusbar_progress_bar)