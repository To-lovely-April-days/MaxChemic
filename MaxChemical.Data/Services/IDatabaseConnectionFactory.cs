using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PetaPoco;

namespace MaxChemical.Data.Services
{
    public interface IDatabaseConnectionFactory
    {
        IDatabase CreateConnection();
        Task<T> ExecuteAsync<T>(Func<IDatabase, Task<T>> operation);
        Task ExecuteAsync(Func<IDatabase, Task> operation);
    }
}
