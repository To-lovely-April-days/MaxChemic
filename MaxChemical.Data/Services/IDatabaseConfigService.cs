using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Data.Services
{
    public interface IDatabaseConfigService
    {
        string GetConnectionString();
        bool TestConnection();
    }

}
