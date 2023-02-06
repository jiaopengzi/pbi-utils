# -*- mode: python ; coding: utf-8 -*-

# 最长为16个字符，不足为自动不全,使用 uuid 后16位
# import uuid
# key = str(uuid.uuid4()).replace("-", "")[:-16]
# print(key)
block_cipher = pyi_crypto.PyiBlockCipher(key='89d96fb04aa74b1a')

#绝对路径
#base_dir = 'C:\\desktop\\pbi-utils-dev\\'

#相对路径
base_dir = ''

added_files = [
         ( base_dir + 'template', 'template' ),
         ( base_dir + 'pbi_tools', 'pbi_tools' )
         ]

a = Analysis(
    ['main.py'],
    pathex=[],
    binaries=[],
    datas=added_files,
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)
pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
	exclude_binaries=True,
    name='pbi-utils',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
#    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=base_dir + 'favicon.ico',
    version=base_dir + 'version.rc',
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='pbi-utils',
)