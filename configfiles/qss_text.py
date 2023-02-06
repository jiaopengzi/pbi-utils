# -*- encoding: utf-8 -*-
"""
@File           :   qss_text.py
@Time           :   2022-11-10, 周四, 21:39
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   qss
"""

# letter-spacing: 1px; 字体间距
QSS = """
QTableWidget {
    font: 75 10pt "微软雅黑";
    font-kerning: none;
}

QTableWidget::item {
    color: rgb(30, 47, 86);
}

QHeaderView::section,
QTableCornerButton:section {
    color: rgb(30, 47, 86);
    font: 75 10pt "微软雅黑";
    font-weight: bold;
    border: 1px solid #dddddd;
    border-left-width: 0px;
    border-right-width: 1px;
    border-top-width: 0px;
    border-bottom-width: 1px;
    background: rgba(30, 47, 86, 5);
}

/*鼠标划过和选择*/
QTableWidget::item:hover {
    color: rgb(30, 47, 86);
    background: #eeeeee;
}

QTableWidget::item:selected {
    color: #FFFFFF;
    background: rgb(120, 170, 220);
}

QToolTip{
    font: 75 10pt "微软雅黑";
    border: 1px solid rgb(111, 156, 207);
    background: white;
    color: rgb(51, 51, 51);
}

QMenuBar {
    font: 75 10pt "微软雅黑";
    /* background: rgb(187, 212, 238); */
    border: 1px solid rgb(111, 156, 207);
    border-left: none;
    border-right: none;
    font-kerning: none;
    color: rgb(30, 47, 86);
}

QMenuBar::item {
    font: 75 10pt "微软雅黑";
    border: 1px solid transparent;
    padding: 5px 10px 5px 10px;
    background: transparent;
    font-kerning: none;
    color: rgb(30, 47, 86);
}

QMenuBar::item:enabled {
    color: rgb(2, 65, 132);
    font-kerning: none;
    color: rgb(30, 47, 86);
}

QMenuBar::item:!enabled {
    color: rgb(155, 155, 155);
    font-kerning: none;
}

QMenuBar::item:enabled:selected {
    border-top-color: rgb(111, 156, 207);
    border-bottom-color: rgb(111, 156, 207);
    background: rgb(198, 224, 252);
    font-kerning: none;
}

QMenu {
    font: 75 10pt "微软雅黑";
    color: rgb(30, 47, 86);
    border: 1px solid ;
}

QStatusBar {
    font: 75 10pt "微软雅黑";
    background: rgb(187, 212, 238);
    border: 1px solid rgb(111, 156, 207);
    border-left: none;
    border-right: none;
    border-bottom: none;
    font-kerning: none;
}

QStatusBar::item {
    font: 75 10pt "微软雅黑";
    border: none;
    border-right: 1px solid rgb(111, 156, 207);
    font-kerning: none;
}

QComboBox {
    font: 75 10pt "微软雅黑";
    height: 30px;
    border-radius: 4px;
    border: 1px solid rgb(111, 156, 207);
    background: white;
    font-kerning: none;
}

/* 鼠标悬浮在 QComboBox 时的状态 */
QComboBox:hover { 
    border: 1px solid rgb(111, 156, 207);
    border-radius: 4px;
    background-color: #EEEEEE;
    color: #298DFF;
    font-kerning: none;
}

QComboBox::drop-down {
    width: 20px;
    border: none;
    /*background: transparent;*/
}

QComboBox::drop-down:hover {
    background: rgba(255, 255, 255, 30);
}

QComboBox QAbstractItemView {
    border: 1px solid rgb(111, 156, 207);
    background: white;
    outline: none;
}

QComboBox QAbstractItemView::item {
    height: 25px;
    color: rgb(73, 73, 73);
}

QComboBox QAbstractItemView::item:selected {
    background: rgb(232, 241, 250);
    color: rgb(2, 65, 132);
}
 
QProgressBar{
    font: 75 10pt "微软雅黑";
    border: none;
    text-align: center;
    color: white;
    background: rgb(173, 202, 232);
}

QProgressBar::chunk {
    background: rgb(16, 135, 209);
}

QCheckBox{
    font: 75 10pt "微软雅黑";
    spacing: 5px;
}

QCheckBox:enabled:checked{
    color: rgb(2, 65, 132);
}

QCheckBox:enabled:!checked{
    color: rgb(70, 71, 73);
}

QCheckBox:enabled:hover{
    color: rgb(0, 78, 161);
}

QCheckBox:!enabled{
    color: rgb(80, 80, 80);
}

QCheckBox::indicator {
    width: 20px;
    height: 20px;
}

QRadioButton{
    font: 75 10pt "微软雅黑";
    spacing: 5px;
}

QRadioButton:enabled:checked{
    color: rgb(2, 65, 132);
}

QRadioButton:enabled:!checked{
    color: rgb(70, 71, 73);
}

QRadioButton:enabled:hover{
    color: rgb(0, 78, 161);
}

QRadioButton:!enabled{
    color: rgb(80, 80, 80);
}

QRadioButton::indicator {
    width: 20px;
    height: 20px;
}
 
QLineEdit {
    font: 75 10pt "微软雅黑";
    border-radius: 4px;
    height: 30px;
    border: 1px solid rgb(111, 156, 207);
    background: white;
    font-kerning: none;
}

/* 鼠标悬浮在 QLineEdit 时的状态 */
QLineEdit:hover { 
    font: 75 10pt "微软雅黑";
    border: 1px solid rgb(111, 156, 207);
    border-radius: 4px;
    background-color: #EEEEEE;
    font-kerning: none;
}

QTextEdit {
    font: 75 10pt "微软雅黑";
    border: 1px solid rgb(111, 156, 207);
    color: rgb(70, 71, 73);
    background: rgb(187, 212, 238);
}

QLabel {
    font: 75 10pt "微软雅黑";
    color: rgb(30, 47, 86);
    height: 30px;
    font-kerning: none;
}

QToolButton{
    font: 75 10pt "微软雅黑";
    height: 56px;
    color: rgb(30, 47, 86);
    background: transparent;
    qproperty-iconSize: 32px 32px;
    qproperty-toolButtonStyle: ToolButtonTextUnderIcon;
} 
  
QToolButton:hover {
    background: rgb(187, 212, 238);
}

QToolBar { 
    background: rgba(30, 47, 86, 20);
}

QPushButton{
    font: 75 10pt "微软雅黑";
    border-radius: 4px;
    border: none;
    width: 96px;
    height: 30px;
    font-kerning: none;
}

QPushButton:enabled {
    background: transparent;
    color: 888888;
    font-kerning: none;
}

QPushButton:!enabled {
    color: rgba(30, 47, 86, 88);
    font-kerning: none;
}

QPushButton:enabled:hover {
    background: rgba(54, 144, 207, 90);
    font-kerning: none;
}

QPushButton:enabled:pressed {
        background: rgb(54, 144, 207);
        color: white;
        font-kerning: none;
}

QTextBrowser {
    font: 75 10pt "微软雅黑";
    background: transparent;
    border: 1px solid rgb(30, 47, 86);
    border-radius: 8px;
    font-kerning: none;
}

QPlainTextEdit {
    font: 75 10pt "微软雅黑";
    background: transparent;
    border: 1px solid rgba(30, 47, 86, 88);
    border-radius: 4px;
    font-kerning: none;
}
"""