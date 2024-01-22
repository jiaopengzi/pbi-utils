## 1、Python 版本：3.9.13



## 2、拉取并进入目录

```shell
# 国内 gitee
git clone git@gitee.com:jiaopengzi/pbi-utils.git

# github
git clone git@gitee.com:jiaopengzi/pbi-utils.git

cd .\pbi-utils\
```



## 3、创建虚拟环境并激活

```shell
# 创建虚拟环境
python -m venv .venv

# 激活虚拟环境
.\.venv\Scripts\activate

# 退出虚拟环境
.\.venv\Scripts\deactivate
```



## 4、安装依赖包

```shell
# 查看是否为纯净虚拟环境
pip list

# 安装依赖包
pip install -r requirements.txt
```



**注意**：pyinstaller 使用 [upx](https://github.com/upx/upx/releases/) 压缩, 需要手动下载最新的 windows 版本的 upx 存放到 `.\.venv\Scripts\` 目录下。



## 5、打包代码为执行文件

```shell
# 打包为文件夹形式
pyinstaller.exe .\main_folder.spec

# 打包为独立的 exe, 独立exe执行的时候会解压到执行目录，需要根据需求平衡启动效率的问题。
pyinstaller.exe .\main_single_exe.spec
```



## 6、使用 NSIS 生成安装包和便携式压缩包

```shell
python .\create_install_file.py
```

**注意**：需要提前安装 NSIS,链接：https://nsis.sourceforge.io/Main_Page

生成的安装包在项目文件夹平级目录的`pbi-utils-release`

## 
