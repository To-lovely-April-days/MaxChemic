-- ============================================================================
--  MaxChemical 建库与账号(两种模式都执行)
--  由 mysql-setup.bat 替换占位符后执行(__APPPWD__ / __ROPWD__)。
--
--  建两个账号,刻意不让主程序用 root:
--    maxchemical —— 只对本库有读写权,拿不到其他库,也没有 GRANT 权
--    mcp_ro      —— 只有 SELECT,给 AI 查询接口用
--
--  这一层是真正的那道门。MCP 程序里的只读 SQL 校验只是兜底——
--  就算校验被绕过、就算将来有人改代码加了写入口,账号权限照样挡得住。
--
--  注意:本文件**不碰 root 口令**。使用客户现有实例时改动 root 是灾难性的。
--  内置模式下的 root 口令设置在 init-db-embedded.sql 里,只在那个模式执行。
-- ============================================================================

CREATE DATABASE IF NOT EXISTS maxchemical
    DEFAULT CHARACTER SET utf8mb4
    DEFAULT COLLATE utf8mb4_unicode_ci;

-- ── 应用账号:本库读写 ──────────────────────────────────────────────
-- 只建 @'localhost' 与 @'127.0.0.1',不建 @'%'——
-- 后者等于允许从任意 IP 登录,本机部署完全用不上。
CREATE USER IF NOT EXISTS 'maxchemical'@'localhost' IDENTIFIED BY '__APPPWD__';
CREATE USER IF NOT EXISTS 'maxchemical'@'127.0.0.1' IDENTIFIED BY '__APPPWD__';
ALTER  USER 'maxchemical'@'localhost' IDENTIFIED BY '__APPPWD__';
ALTER  USER 'maxchemical'@'127.0.0.1' IDENTIFIED BY '__APPPWD__';

GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, ALTER, INDEX, REFERENCES
    ON maxchemical.* TO 'maxchemical'@'localhost';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, ALTER, INDEX, REFERENCES
    ON maxchemical.* TO 'maxchemical'@'127.0.0.1';

-- ── 只读账号:给 MCP 历史查询接口 ────────────────────────────────────
CREATE USER IF NOT EXISTS 'mcp_ro'@'localhost' IDENTIFIED BY '__ROPWD__';
CREATE USER IF NOT EXISTS 'mcp_ro'@'127.0.0.1' IDENTIFIED BY '__ROPWD__';
ALTER  USER 'mcp_ro'@'localhost' IDENTIFIED BY '__ROPWD__';
ALTER  USER 'mcp_ro'@'127.0.0.1' IDENTIFIED BY '__ROPWD__';

GRANT SELECT ON maxchemical.* TO 'mcp_ro'@'localhost';
GRANT SELECT ON maxchemical.* TO 'mcp_ro'@'127.0.0.1';

FLUSH PRIVILEGES;
