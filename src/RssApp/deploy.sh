#!/bin/zsh

rm -f archive.zip
rm -rf bin/release/net9.0/win-x64/publish
dotnet publish -c Release -r win-x64 --output bin/release/net9.0/win-x64/publish

pushd bin/release/net9.0/win-x64/publish/
zip -r ../../../../../archive.zip ./*
popd

az webapp deploy --resource-group rss-reader  --name rssweb  --src-path archive.zip 
