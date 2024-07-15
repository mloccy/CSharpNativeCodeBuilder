all: clean
	dotnet pack -c Release NativeCodeBuilder/Artomatix.NativeCodeBuilder

clean:
	dotnet clean NativeCodeBuilder/Artomatix.NativeCodeBuilder 
