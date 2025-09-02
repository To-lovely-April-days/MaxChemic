


USE maxchemical;

-- 实验记录主表
CREATE TABLE experiment_records (
    id INT AUTO_INCREMENT PRIMARY KEY,
    experiment_id VARCHAR(50) NOT NULL UNIQUE,
    experiment_name VARCHAR(200) NOT NULL,
    flow_name VARCHAR(200) NOT NULL,
    start_time DATETIME NOT NULL,
    end_time DATETIME NULL,
    duration_seconds DOUBLE NULL,
    is_successful BOOLEAN NOT NULL DEFAULT FALSE,
    total_nodes INT NOT NULL DEFAULT 0,
    completed_nodes INT NOT NULL DEFAULT 0,
    error_message TEXT NULL,
    simulation_mode BOOLEAN NOT NULL DEFAULT FALSE,
    operator_name VARCHAR(100) NULL,
    description TEXT NULL,
    tags VARCHAR(500) NULL,
    created_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_start_time (start_time),
    INDEX idx_experiment_name (experiment_name),
    INDEX idx_is_successful (is_successful),
    INDEX idx_simulation_mode (simulation_mode)
);

-- 节点执行记录表
CREATE TABLE experiment_node_records (
    id INT AUTO_INCREMENT PRIMARY KEY,
    experiment_id VARCHAR(50) NOT NULL,
    node_id VARCHAR(50) NOT NULL,
    node_name VARCHAR(200) NOT NULL,
    node_type VARCHAR(50) NOT NULL,
    device_id VARCHAR(50) NULL,
    device_name VARCHAR(200) NULL,
    command_name VARCHAR(100) NULL,
    start_time DATETIME NOT NULL,
    end_time DATETIME NULL,
    duration_ms DOUBLE NULL,
    is_successful BOOLEAN NOT NULL DEFAULT FALSE,
    error_message TEXT NULL,
    input_parameters TEXT NULL,
    output_parameters TEXT NULL,
    execution_order INT NOT NULL DEFAULT 0,
    created_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_experiment_id (experiment_id),
    INDEX idx_device_id (device_id),
    INDEX idx_start_time (start_time),
    FOREIGN KEY (experiment_id) REFERENCES experiment_records(experiment_id) ON DELETE CASCADE
);

-- 设备数据记录表（实时记录所有数据）
CREATE TABLE experiment_device_data (
    id INT AUTO_INCREMENT PRIMARY KEY,
    experiment_id VARCHAR(50) NOT NULL,
    device_id VARCHAR(50) NOT NULL,
    device_name VARCHAR(200) NOT NULL,
    parameter_name VARCHAR(100) NOT NULL,
    parameter_value TEXT NOT NULL,
    parameter_type VARCHAR(50) NOT NULL,
    unit VARCHAR(20) NULL,
    timestamp DATETIME NOT NULL,
    command_name VARCHAR(100) NULL,
    node_id VARCHAR(50) NULL,
    node_name VARCHAR(200) NULL,
    data_direction ENUM('INPUT', 'OUTPUT') NOT NULL DEFAULT 'OUTPUT',
    created_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_experiment_id (experiment_id),
    INDEX idx_device_id (device_id),
    INDEX idx_timestamp (timestamp),
    INDEX idx_export_query (experiment_id, device_id, parameter_name, data_direction, timestamp),
    FOREIGN KEY (experiment_id) REFERENCES experiment_records(experiment_id) ON DELETE CASCADE
);

-- 变量记录表
CREATE TABLE experiment_variables (
    id INT AUTO_INCREMENT PRIMARY KEY,
    experiment_id VARCHAR(50) NOT NULL,
    variable_name VARCHAR(100) NOT NULL,
    variable_value TEXT NOT NULL,
    variable_type VARCHAR(50) NOT NULL,
    source_node_id VARCHAR(50) NULL,
    source_device_id VARCHAR(50) NULL,
    timestamp DATETIME NOT NULL,
    created_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_experiment_id (experiment_id),
    INDEX idx_variable_name (variable_name),
    INDEX idx_timestamp (timestamp),
    FOREIGN KEY (experiment_id) REFERENCES experiment_records(experiment_id) ON DELETE CASCADE
);