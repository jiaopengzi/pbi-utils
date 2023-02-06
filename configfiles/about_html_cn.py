# -*- encoding: utf-8 -*-
"""
@File           :   about_html_en.py
@Time           :   2022-11-10, 周四, 21:44
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   中文 关于页面 html
"""
ABOUT_HTML = """
<html>

<head>
    <style type="text/css">
        body {
            font-family: 'Microsoft YaHei';
            font-size: 12pt;
            line-height: 1.5;
            font-weight: 400;
            font-style: normal;
            color: #1e2f56;
        }
        
        p {
            white-space: pre-wrap;
            margin-top: 0px;
            margin-bottom: 0px;
            margin-left: 0px;
            margin-right: 0px;
            text-indent: 0px;
        }

        .p-url {
            font-size: 12pt;
            text-decoration: underline;
        }
    </style>
</head>

<body>
    <p class="p-text">版本: ${version}</p>
    <p ss="p-text">日期: ${release_date}</p>
    <p class="p-text">作者: 焦棚子</p>
    <p class="p-text">邮箱: jiaopengzi@qq.com</p>

    <p class="p-url"><a href="https://jiaopengzi.com/">博客: https://jiaopengzi.com/</a></p>
    <p class="p-url"><a href="https://jiaopengzi.com/2880.html">文档: 点击查看</a></p>
    <p class="p-url"><a href="https://jiaopengzi.com/all-course">Power BI 视频课程: 点击学习</a></p>
    <p class="p-url"><a href="https://pbi.tools/">power by pbi-tools</a></p>
</body>

</html>
"""