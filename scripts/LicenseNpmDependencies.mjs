/**
 * FilePath    : pbi-utils\scripts\LicenseNpmDependencies.mjs
 * Author      : jiaopengzi
 * Blog        : https://jiaopengzi.com
 * Copyright   : Copyright (c) 2026 by jiaopengzi, All Rights Reserved.
 * Description : 离线解析已安装前端生产依赖及 peer 闭包, 拒绝越界链接和缺失依赖.
 */
import fs from 'node:fs';
import path from 'node:path';

const webRoot = fs.realpathSync(process.argv[2]);
const modulesRoot = fs.realpathSync(path.join(webRoot, 'node_modules'));
const packages = new Map();
const visitedDirectories = new Set();
const lock = fs.readFileSync(path.join(webRoot, 'pnpm-lock.yaml'), 'utf8');
const installedLock = fs.readFileSync(path.join(modulesRoot, '.pnpm', 'lock.yaml'), 'utf8');

/** 判断精确包版本存在于锁文件 packages/snapshots 键中; 不解析或改写 YAML. */
function locked(name, version, content) {
    const escaped = `${name}@${version}`.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(`^  ['\"]?${escaped}(?:['\"]?:|\\()`, 'm').test(content);
}

/** 核验已解析路径属于依赖目录, 输入为绝对路径; 越界时抛错. */
function assertInside(candidate) {
    const relative = path.relative(modulesRoot, candidate);
    if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
        throw new Error(`npm dependency escapes node_modules: ${candidate}`);
    }
}

/** 从引用包向上解析依赖目录, 返回物理路径; 缺少必需依赖或越界时抛错. */
function resolvePackage(name, parent, optional) {
    if (!/^(?:@[a-z0-9._-]+\/)?[a-z0-9._-]+$/i.test(name)) throw new Error(`Invalid npm name: ${name}`);
    let current = parent;
    while (true) {
        const candidate = path.join(current, 'node_modules', name);
        if (fs.existsSync(candidate)) {
            const actual = fs.realpathSync(candidate);
            assertInside(actual);
            return actual;
        }
        if (current === webRoot) break;
        const next = path.dirname(current);
        if (next === current || !current.startsWith(webRoot + path.sep)) break;
        current = next;
    }
    if (optional) return null;
    throw new Error(`Missing production npm dependency: ${name} (from ${parent})`);
}

/** 读取一个包及其生产/peer 依赖, 返回清单中的精确身份; 错误不会降级为空闭包. */
function visit(name, parent, optional = false, expectedVersion = null, specifier = '') {
    const directory = resolvePackage(name, parent, optional);
    if (!directory) return;
    const metadataPath = path.join(directory, 'package.json');
    assertInside(fs.realpathSync(metadataPath));
    const metadata = JSON.parse(fs.readFileSync(metadataPath, 'utf8'));
    const alias = /^npm:((?:@[^/]+\/)?[^@]+)@/.exec(specifier);
    const expectedName = alias ? alias[1] : name;
    if (metadata.name !== expectedName || !metadata.version) throw new Error(`npm identity mismatch: ${name}`);
    name = metadata.name;
    if (expectedVersion && metadata.version !== expectedVersion) throw new Error(`npm installed version mismatch: ${name}`);
    const key = `${name}/${metadata.version}`;
    if (!locked(name, metadata.version, lock) || !locked(name, metadata.version, installedLock)) throw new Error(`npm version is absent from lockfiles: ${key}`);
    if (visitedDirectories.has(directory)) return key;
    visitedDirectories.add(directory);
    if (!packages.has(key)) packages.set(key, { name, version: metadata.version, directories: [], license: metadata.license ?? '',
        copyright: metadata.copyright ?? '', author: metadata.author ?? '',
        source: metadata.repository ?? metadata.homepage ?? '', dependencies: [] });
    if (packages.get(key).license !== (metadata.license ?? '')) throw new Error(`Conflicting npm licenses: ${key}`);
    packages.get(key).directories.push(directory);
    const required = { ...metadata.dependencies, ...metadata.peerDependencies };
    const optionalNames = new Set(Object.keys(metadata.optionalDependencies ?? {}));
    for (const [dependency, info] of Object.entries(metadata.peerDependenciesMeta ?? {})) {
        if (info.optional && !metadata.dependencies?.[dependency]) optionalNames.add(dependency);
    }
    for (const dependency of Object.keys({ ...required, ...metadata.optionalDependencies }).sort()) {
        const child = visit(dependency, directory, optionalNames.has(dependency), null, required[dependency] ?? metadata.optionalDependencies?.[dependency]);
        if (child && !packages.get(key).dependencies.includes(child)) packages.get(key).dependencies.push(child);
    }
    return key;
}

const root = JSON.parse(fs.readFileSync(path.join(webRoot, 'package.json'), 'utf8'));
const roots = [];
for (const [name, version] of Object.entries(root.dependencies ?? {}).sort()) {
    if (!/^\d+\.\d+\.\d+(?:-[a-z0-9.-]+)?$/i.test(version)) throw new Error(`Production dependency must be exact: ${name}`);
    roots.push(visit(name, webRoot, false, version));
}
if (!roots.length) throw new Error('No frontend production dependencies were found.');
process.stdout.write(JSON.stringify({ roots, packages: [...packages.values()].sort((a, b) => `${a.name}/${a.version}`.localeCompare(`${b.name}/${b.version}`, 'en')) }));
