# Radio Zapper

Radio Zapper é um player desktop de rádios online. Ele reproduz streams de áudio, mantém a ordem escolhida para as estações e permite navegar com os botões de mídia do teclado, mesmo com a janela sem foco quando o Windows direciona os comandos para sua sessão de mídia.

## Tecnologias

- C# e .NET 10
- Avalonia UI 12 com FluentTheme
- CommunityToolkit.Mvvm
- LibVLCSharp 3 e bibliotecas nativas VideoLAN.LibVLC.Windows
- Arquivos JSON para persistência

O player usa LibVLC apenas para áudio. Não há componente visual de vídeo, WebView, Electron ou Node.js.

## Executar e compilar

É necessário ter o SDK estável do .NET 10. Na pasta do projeto:

```powershell
dotnet restore
dotnet run
```

Para compilar:

```powershell
dotnet build
```

No Windows, execute `compilar.bat` e escolha **1** para compilar em Debug ou **2** para publicar em `bin\publish-win-x64-sem-runtime`. A opção 2 reúne as dependências gerenciadas no executável e deixa as bibliotecas nativas do VLC na pasta `libvlc\win-x64`, onde o LibVLCSharp consegue encontrá-las. Ela **não inclui o runtime do .NET**. Na máquina de destino, instale o runtime .NET 10 x64.

O comando equivalente à opção 2 é:

```powershell
dotnet publish RadioZapper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:VlcWindowsX64Enabled=true -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false -p:DebugType=None -p:DebugSymbols=false -o bin\publish-win-x64-sem-runtime
```

Distribua o conteúdo da pasta publicada, inclusive `libvlc\win-x64`. Copiar somente o `.exe` impede a reprodução e pode impedir a inicialização. O script remove os símbolos de depuração nativos (`.pdb`) que não são necessários para executar o aplicativo.

## Uso

Na janela compacta do player, clique em **Minhas rádios** e depois em **+ Adicionar rádio**. Informe um nome e uma URL HTTP ou HTTPS de stream e salve. Localização, logotipo e favorito são opcionais. O logotipo pode ser uma URL de imagem ou um arquivo local selecionado em **Procurar**. Uma cópia da imagem local é guardada nos dados do aplicativo.

Para encontrar uma rádio no catálogo RadiosNet, clique em **Buscar rádios** na janela principal, digite o nome e escolha **Pesquisar**. Os resultados podem ser ampliados com **Carregar mais**. Clique em **Tocar** para ouvir sem cadastrar ou em **Salvar** para abrir o cadastro com os dados preenchidos. Enquanto uma rádio encontrada estiver tocando sem cadastro, o botão **Salvar rádio** aparece no player para permitir salvá-la depois. Confira os campos antes de confirmar.

Para importar, cole no campo **Link para importar** a página de uma rádio no Radios.com.br ou um link HTTP/HTTPS para um arquivo `.pls` e clique em **Buscar dados**. O aplicativo tenta preencher nome, stream direto, cidade/estado e logotipo. Se a página do Radios.com.br bloquear a leitura, a playlist ainda pode fornecer o stream, e o aplicativo tenta buscar os dados descritivos no RadiosNet. Um `.pls` de outro site pode trazer apenas o stream e, às vezes, o nome. Revise os campos antes de salvar. Se as fontes estiverem indisponíveis, complete o cadastro manualmente. A importação de páginas é específica desses sites e pode precisar de atualização se eles mudarem sua estrutura.

Clique em **Tocar** na janela de gerenciamento para iniciar a rádio e voltar ao player. O nome da estação seleciona sem reproduzir. Os controles centrais alternam reprodução e pausa, e avançam ou voltam na ordem exibida. Os botões de seta em cada rádio alteram essa ordem; ao chegar ao fim, **Próxima estação** volta à primeira rádio.

Durante a reprodução de uma rádio encontrada e ainda não salva, **Play/Pause** controla essa rádio. **Anterior** passa para a última rádio salva e **Próxima** para a primeira. Rádios temporárias não são restauradas quando o aplicativo reinicia.

Como rádios transmitem áudio ao vivo, **Pause** interrompe o stream e **Play** reconecta ao ponto atual da transmissão. Se a conexão falhar, a interface mostrará o erro e continuará utilizável.

Minimizar ou fechar a janela a esconde na bandeja do sistema por padrão. Clique duas vezes no ícone para restaurá-la. O menu da bandeja oferece abrir, rádio atual, Play/Pause, anterior, próxima e sair. Em **Configurações**, é possível escolher o tema, iniciar minimizado e mudar o comportamento do fechamento.

## Dados e arquitetura

No Windows, os dados ficam em `%LOCALAPPDATA%\RadioZapper`:

```text
stations.json
settings.json
logos/
```

Os arquivos não são gravados ao lado do executável. A última estação e o volume são restaurados; a reprodução não começa automaticamente. A lista inicia vazia, sem URLs de exemplo inventadas.

Os ViewModels lidam com comandos e apresentação; `StationService` cuida da navegação e da ordem; `RadioPlayerService` usa LibVLCSharp; repositório e serviço de configurações isolam os arquivos JSON. Os serviços de mídia ficam atrás de `IMediaKeyService`, deixando a lógica do player independente da integração específica do Windows.

## Teclas multimídia no Windows

O Radio Zapper cria uma sessão nos **Controles de Transporte de Mídia do Sistema** (SMTC) do Windows. Os comandos físicos Play/Pause, Next Track e Previous Track são convertidos nos mesmos comandos usados na interface. O aplicativo não instala um hook global de teclado. Se houver vários players abertos, o Windows escolhe qual sessão recebe as teclas. A sessão é mantida enquanto o processo permanece na bandeja.

O início automático com o Windows não faz parte deste MVP. Ao publicar para Linux ou macOS no futuro, será necessário fornecer as bibliotecas nativas do LibVLC e uma implementação de `IMediaKeyService` para a plataforma.
