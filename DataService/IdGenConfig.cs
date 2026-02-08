using IdGen;
using System;
using System.Collections.Generic;
using System.Text;

namespace DataService;

internal static class IdGenConfig
{
    public static readonly IdGeneratorOptions idGeneratorOptions = new IdGeneratorOptions(idStructure:new IdStructure(41, 5, 17));
}
