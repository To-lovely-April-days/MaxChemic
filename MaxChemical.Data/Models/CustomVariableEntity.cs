using PetaPoco;
using System;

namespace MaxChemical.Data.Models
{
    [TableName("custom_variables")]
    [PrimaryKey("id", AutoIncrement = true)]
    public class CustomVariableEntity
    {
        [Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string Name { get; set; }

        [Column("value")]
        public string Value { get; set; } // 存储为JSON字符串

        [Column("type")]
        public string Type { get; set; }

        [Column("created_time")]
        public DateTime CreatedTime { get; set; }

        [Column("last_modified_time")]
        public DateTime LastModifiedTime { get; set; }

        [Column("is_from_execution_context")]
        public bool IsFromExecutionContext { get; set; }

        [Column("description")]
        public string Description { get; set; }

        [Column("category")]
        public string Category { get; set; }

    
    }

    // Models/CustomVariableHistoryEntity.cs
    [TableName("custom_variable_history")]
    [PrimaryKey("id", AutoIncrement = true)]
    public class CustomVariableHistoryEntity
    {
        [Column("id")]
        public int Id { get; set; }

        [Column("variable_id")]
        public int VariableId { get; set; }

        [Column("old_value")]
        public string OldValue { get; set; }

        [Column("new_value")]
        public string NewValue { get; set; }

        [Column("operation_type")]
        public string OperationType { get; set; } // CREATE, UPDATE, DELETE

        [Column("operation_time")]
        public DateTime OperationTime { get; set; }

        [Column("operation_user")]
        public string OperationUser { get; set; }
    }

    // Models/CustomVariableData.cs (业务模型)
    public class CustomVariableData
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public object Value { get; set; }
        public string Type { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastModifiedTime { get; set; }
        public bool IsFromExecutionContext { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
      
    }
}
