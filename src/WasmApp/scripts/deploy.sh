# Build and package
RELEASE_DEBUG="release"
NET_VERSION="net9.0"
PLATFORM="win-x64" #linux-x64

rm -rf bin/
rm -rf obj/
rm -f archive.zip
rm -rf bin/$RELEASE_DEBUG/$NET_VERSION/$PLATFORM/publish

dotnet build -c $RELEASE_DEBUG -r $PLATFORM ../Shared/Shared.csproj
dotnet publish -c $RELEASE_DEBUG -r $PLATFORM WasmApp.csproj --output bin/$RELEASE_DEBUG/$NET_VERSION/$PLATFORM/publish --self-contained true

pushd bin/$RELEASE_DEBUG/$NET_VERSION/$PLATFORM/publish/
zip -rv ../../../../../archive.zip ./*
popd

# save the token in dotnet secrets 
DEPLOYMENT_TOKEN=$(dotnet user-secrets list | grep "DeploymentToken" | cut -d ' ' -f 3)

# deploy the zip to the static web app rssServer inside the rsswasm resource group using AZ CLI
swa deploy ./bin/$RELEASE_DEBUG/$NET_VERSION/$PLATFORM/publish/wwwroot --deployment-token $DEPLOYMENT_TOKEN --env production