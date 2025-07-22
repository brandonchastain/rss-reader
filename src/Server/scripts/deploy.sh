# Build and package
RELEASE_DEBUG="debug"
PLATFORM="win-x64" #linux-x64
rm -rf bin/
rm -rf obj/
rm -f archive.zip
rm -rf bin/$RELEASE_DEBUG/net9.0/$PLATFORM/publish

dotnet build -c $RELEASE_DEBUG -r $PLATFORM ../Shared/Shared.csproj
dotnet publish -c $RELEASE_DEBUG -r $PLATFORM Server.csproj --output bin/$RELEASE_DEBUG/net9.0/$PLATFORM/publish --self-contained true

pushd bin/$RELEASE_DEBUG/net9.0/$PLATFORM/publish/
zip -rv ../../../../../archive.zip ./*
popd

# Set to 64-bit explicitly
az webapp config set --name rssServer --resource-group rsswasm --use-32bit-worker-process false

# deploy the zip to the web app rssServer inside the rsswasm resource group using AZ CLI
az webapp deploy \
    --resource-group rsswasm \
    --name rssServer \
    --src-path archive.zip