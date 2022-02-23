DLL = bin/Debug/netcoreapp3.1/PatchPig.dll 

$(DLL): $(wildcard *.cs)
	dotnet build

run: $(DLL)
	dotnet $(DLL)
