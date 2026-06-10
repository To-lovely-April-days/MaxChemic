using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Data.Models;

namespace MaxChemical.Data.Services
{
    public interface ICustomVariablePersistenceService
    {
        Task<List<CustomVariableData>> LoadCustomVariablesAsync();
        Task<CustomVariableData> GetCustomVariableAsync(string name);
        Task<int> SaveCustomVariableAsync(CustomVariableData variable);
        Task<bool> UpdateCustomVariableAsync(CustomVariableData variable);
        Task<bool> DeleteCustomVariableAsync(string name);
        Task<bool> DeleteCustomVariableAsync(int id);
        Task SaveCustomVariablesAsync(IEnumerable<CustomVariableData> variables);
        Task<List<CustomVariableData>> GetVariablesByCategoryAsync(string category);
        Task<bool> VariableExistsAsync(string name);
        Task InitializeDatabaseAsync();
    }
}
