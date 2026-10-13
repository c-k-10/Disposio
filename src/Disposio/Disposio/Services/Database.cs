//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using Npgsql;

<<<<<<< HEAD
//namespace Disposio.Services
//{
//    internal class Database
//    {
//        private readonly string _connectionString =
//        "Host=ep-curly-queen-b2qa2c1e-pooler.c-6.eu-central-1.aws.neon.tech; Database=neondb; Username=neondb_owner; Password=npg_4pc0hjEnJIwk; SSL Mode=VerifyFull; Channel Binding=Require;"
=======
namespace Disposio.Services
{
    internal class Database
    {
        private readonly string _connectionString =
        "Host=ep-curly-queen-b2qa2c1e-pooler.c-6.eu-central-1.aws.neon.tech; Database=neondb; Username=neondb_owner; Password=npg_4pc0hjEnJIwk; SSL Mode=VerifyFull; Channel Binding=Require;";
>>>>>>> 9ef0d635b931af1ca0360d3721c7aaaabd549de3

//        public NpgsqlConnection GetConnection()
//        {
//            var conn = new NpgsqlConnection(_connectionString);
//            conn.Open();
//            return conn;
//        }
//    }
//}
