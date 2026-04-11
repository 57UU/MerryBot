cd Test
dotnet build -c Release
if [ -d "bin/linux" ]; then
    cd bin/linux/Release/net10.0
else
    cd bin/Release/net10.0
fi
./Test
