using LiteDB;
using NapcatClient;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GroupHistoryRecorder;

public class HistoryRecorder : IDisposable
{
    LiteDatabase database;
    
    public HistoryRecorder(string dbPath)
    {
        database = new LiteDatabase(dbPath);
    }
   
    public void Dispose()
    {
        database.Dispose();
    }
}
