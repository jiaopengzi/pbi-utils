# -*- encoding: utf-8 -*-
"""
@File           :   msg_config_cn.py
@Time           :   2022-11-10, 周四, 21:39
@Author         :   焦棚子
@Email          :   jiaopengzi@qq.com
@Blog           :   https://jiaopengzi.com/
@Version        :   1.0.0
@Description    :   中文消息配置
"""

MSG = {

        # "dialog_text"
        "msg0001": {"title": "0001", "msg": "列数不符"},
        "msg0002": {"title": "0002", "msg": "第 ${row} 行,第 ${col} 列,数据为空"},
        "msg0003": {"title": "0003", "msg": "第 ${row} 行,第 ${col} 列,\n值不属于设定的范围：${rls_value}"},
        "msg0004": {"title": "0004", "msg": "请填写对应数据"},
        "msg0005": {"title": "0005", "msg": "第 ${row} 行,第 ${col} 列,\n请输入：True 或者 False"},

        # "multi_line"
        "msg0101": {"title": "0101", "msg": "左侧请选择需要添加内容"},
        "msg0102": {"title": "0102", "msg": "度量值表:${value}"},
        "msg0103": {"title": "0103", "msg": "请在右侧添加内容"},

        # "ui_01"
        "msg0201": {"title": "0201", "msg": "${value}"},
        "msg0202": {"title": "0202", "msg": "${value}"},
        "msg0203": {"title": "0203", "msg": "${value}"},
        "msg0204": {"title": "0204", "msg": "未知错误-ui_01_QPushButton_init_clicked"},
        "msg0205": {"title": "0205", "msg": "初始化完毕"},
        "msg0206": {"title": "0206", "msg": "未知错误-create_json_signal_dic_callback"},

        # "ui_02"
        "msg0301": {"title": "0301", "msg": "${value}"},
        "msg0302": {"title": "0302", "msg": "编辑后请保存"},
        "msg0303": {"title": "0303", "msg": "编辑后请保存"},
        "msg0304": {"title": "0304", "msg": "${value}"},
        "msg0305": {"title": "0305", "msg": "数据加载失败，请确保文件路径正确"},
        "msg0306": {"title": "0306", "msg": "${value}"},
        "msg0307": {"title": "0307", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0308": {"title": "0308", "msg": "${value}"},
        "msg0309": {"title": "0309", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0310": {"title": "0310", "msg": "数据写入失败！\n第 ${row} 行\n第 ${col} 列\n数据为空！"},
        "msg0311": {"title": "0311", "msg": "数据写入失败！\n第 ${row_old} 行\n第 ${row} 行\n第 ${col} 列\n数据重复！"},
        "msg0312": {"title": "0312", "msg": "数据已经保存"},

        # "ui_03"
        "msg0401": {"title": "0401", "msg": "${value}"},
        "msg0402": {"title": "0402", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0403": {"title": "0403", "msg": "${value}"},
        "msg0404": {"title": "0404", "msg": "数据已经保存"},
        "msg0405": {"title": "0405", "msg": "数据加载失败，请确保文件路径正确"},
        "msg0406": {"title": "0406", "msg": "${value}"},
        "msg0407": {"title": "0407", "msg": "数据写入失败！\n第 ${row} 行\n第 ${col} 列\n数据为空！"},
        "msg0408": {"title": "0408", "msg": "数据写入失败！\n第 ${row_old} 行\n第 ${row} 行\n第 ${col} 列\n数据重复！"},

        # "ui_04"
        "msg0501": {"title": "0501", "msg": "${value}"},
        "msg0502": {"title": "0502", "msg": "数据加载失败，请确保文件路径正确"},
        "msg0503": {"title": "0503", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0504": {"title": "0504", "msg": "${value}"},
        "msg0505": {"title": "0505", "msg": "${value}"},
        "msg0506": {"title": "0506", "msg": "${value}"},
        "msg0507": {"title": "0507", "msg": "${value}"},
        "msg0508": {"title": "0508", "msg": "${value}"},
        "msg0509": {"title": "0509", "msg": "未知错误-btn7_clicked_permission_init"},
        "msg0510": {"title": "0510", "msg": "数据写入失败，请确保文件路径正确或者名称是否存在"},
        "msg0511": {"title": "0511", "msg": "${value}"},
        "msg0512": {"title": "0512", "msg": "${value}"},
        "msg0513": {"title": "0513", "msg": "未知错误-btn8_clicked_permission_init"},
        "msg0514": {"title": "0514", "msg": "数据写入失败，请确保文件路径正确或者名称是否存在"},

        # "ui_05"
        "msg0601": {"title": "0601", "msg": "${value}"},
        "msg0602": {"title": "0602", "msg": "编辑后请保存"},
        "msg0603": {"title": "0603", "msg": "编辑后请保存"},
        "msg0604": {"title": "0604", "msg": "${value}"},
        "msg0605": {"title": "0605", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0606": {"title": "0606", "msg": "${value}"},
        "msg0607": {"title": "0607", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0608": {"title": "0608", "msg": "${value}"},
        "msg0609": {"title": "0609", "msg": "数据写入失败，请确保文件路径正确"},
        "msg0610": {"title": "0610", "msg": "数据写入失败！\n第 ${row} 行\n第 ${col} 列\n数据为空！"},
        "msg0611": {"title": "0611", "msg": "数据已经保存"},

        # "ui_06"
        "msg0701": {"title": "0701", "msg": "数据加载失败，请确保文件路径正确"},
        "msg0702": {"title": "0702", "msg": "${value}"},
        "msg0703": {"title": "0703", "msg": "${value}"},
        "msg0704": {"title": "0704", "msg": "${value}"},
        "msg0705": {"title": "0705", "msg": "${value}"},
        "msg0706": {"title": "0706", "msg": "${value}"},
        "msg0707": {"title": "0707", "msg": "未知错误-ui_06_QPushButton_create_clicked"},
        "msg0708": {"title": "0708", "msg": "pbit创建失败!\n请确认 【pbix 模板】与【配置文件】是否匹配!\n请重新初始化配置文件"},
        "msg0709": {"title": "0709", "msg": "pbit创建成功"},

        # "ui_07"
        "msg0801": {"title": "0801", "msg": "${value}"},
        "msg0802": {"title": "0802", "msg": "${value}"},
        "msg0803": {"title": "0803", "msg": "${value}"},
        "msg0804": {"title": "0804", "msg": "${value}"},
        "msg0805": {"title": "0805", "msg": "未知错误-ui_07_QPushButton_load_clicked"},
        "msg0806": {"title": "0806", "msg": "未知错误-extract_pbix_callback"},
        "msg0807": {"title": "0807", "msg": "请查看:\n${value}"},
        "msg0808": {"title": "0808", "msg": "未知错误-load_multi_line_edit"},
        "msg0809": {"title": "0809", "msg": "pbixA 与 pbixB 不能是同一个文件"},

        # "ui_08"
        "msg0901": {"title": "0901", "msg": "${value}"},
        "msg0902": {"title": "0902", "msg": "${value}"},
        "msg0903": {"title": "0903", "msg": "${value}"},
        "msg0904": {"title": "0904", "msg": "${value}"},
        "msg0905": {"title": "0905", "msg": "未知错误-ui_08_QPushButton_load_clicked"},
        "msg0906": {"title": "0906", "msg": "请查看:\n${value}"},

        # "ui_09"
        "msg1001": {"title": "1001", "msg": "${value}"},
        "msg1002": {"title": "1002", "msg": "未知错误-extract_dax_2_pbix_callback"},
        "msg1003": {"title": "1003", "msg": "${value}"},
        "msg1004": {"title": "1004", "msg": "${value}"},
        "msg1005": {"title": "1005", "msg": "${value}"},
        "msg1006": {"title": "1006", "msg": "${value}"},
        "msg1007": {"title": "1007", "msg": "${value}：没有包含 DAX 文件"},
        "msg1008": {"title": "1008", "msg": "请查看:\n${value}"},
        "msg1009": {"title": "1009", "msg": "未知错误-ui_09_QPushButton_load_clicked"},

        # "validators"
        "msg1101": {"title": "1101", "msg": "不能为空"},
        "msg1102": {"title": "1102", "msg": "不能包含空格"},
        "msg1103": {"title": "1103", "msg": "请输入正确的 pbix 文件"},
        "msg1104": {"title": "1104", "msg": "请输入正确的 pbit 文件"},
        "msg1105": {"title": "1105", "msg": "请输入正确的 json 文件"},
        "msg1106": {"title": "1106", "msg": "请输入正确的文件夹路径"},
        "msg1107": {"title": "1107", "msg": "路径不存在,请输入正确的文件路径"},

        # "about"
        "msg1201": {"title": "1201", "msg": "语言切换成功！\n程序即将重新启动"},

        # 公用错误
        "msg1301": {"title": "1301", "msg": "读取失败, pbix文件或已经损坏！\n使用最新版本Power BI Desktop另存后再试！"},
        "msg1302": {"title": "1302", "msg": "编译失败, pbix文件或已经损坏！\n使用最新版本Power BI Desktop另存后再试！"},
        "msg1303": {"title": "1303", "msg": "拒绝访问！\n请为 pbi-utils 授予相应权限！"},
}